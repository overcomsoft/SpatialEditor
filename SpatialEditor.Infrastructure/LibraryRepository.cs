using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using SpatialEditor.Domain;

namespace SpatialEditor.Infrastructure;

/// <summary>
/// Global equipment block library: classification tree, library items with versioned local-space
/// geometry, and the link from placed block instances to their library item.
/// </summary>
public sealed class LibraryRepository : IAsyncDisposable
{
    public const string UncategorizedName = "미분류";

    // Geometry hash tolerance (metres): shapes equal to the millimetre count as the same block.
    private const string HashOf = "md5(ST_AsText(ST_SnapToGrid({0}, 0.001)))";

    private readonly NpgsqlDataSource dataSource;

    public LibraryRepository(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseNetTopologySuite();
        dataSource = builder.Build();
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    /// <summary>
    /// Creates the library tables and migrates existing <c>block_definitions</c> into library items
    /// (one per block name, shape-hash de-duplicated) and links instances to them. Idempotent.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);
    }

    internal static async Task EnsureSchemaAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var hashOfDefinition = string.Format(HashOf, "d.local_geom");
        var hashOfCandidate = string.Format(HashOf, "s.local_geom");
        var sql = $"""
            CREATE TABLE IF NOT EXISTS block_categories (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                parent_id BIGINT REFERENCES block_categories (id),
                name VARCHAR(255) NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_block_categories_name ON block_categories (COALESCE(parent_id, 0), name);

            CREATE TABLE IF NOT EXISTS block_library (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                code VARCHAR(255) NOT NULL UNIQUE,
                name VARCHAR(255) NOT NULL,
                category_id BIGINT REFERENCES block_categories (id),
                vendor VARCHAR(255),
                model_no VARCHAR(255),
                description TEXT,
                width DOUBLE PRECISION,
                depth DOUBLE PRECISION,
                height DOUBLE PRECISION,
                status VARCHAR(20) NOT NULL DEFAULT 'released',
                current_version INTEGER NOT NULL DEFAULT 1,
                created_by BIGINT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_by BIGINT,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                is_deleted BOOLEAN NOT NULL DEFAULT FALSE
            );

            CREATE TABLE IF NOT EXISTS block_library_versions (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                library_id BIGINT NOT NULL REFERENCES block_library (id),
                version INTEGER NOT NULL,
                local_geom GEOMETRY(Geometry, 5186) NOT NULL,
                local_outer_geom GEOMETRY(Geometry, 5186),
                footprint GEOMETRY(Geometry, 5186),
                base_x DOUBLE PRECISION NOT NULL DEFAULT 0,
                base_y DOUBLE PRECISION NOT NULL DEFAULT 0,
                shape_hash CHAR(32) NOT NULL,
                note TEXT,
                created_by BIGINT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT block_library_versions_unique UNIQUE (library_id, version)
            );
            CREATE INDEX IF NOT EXISTS idx_block_library_versions_hash ON block_library_versions (shape_hash);

            CREATE TABLE IF NOT EXISTS block_ports (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                version_id BIGINT NOT NULL REFERENCES block_library_versions (id) ON DELETE CASCADE,
                name VARCHAR(255) NOT NULL,
                port_type VARCHAR(100),
                local_x DOUBLE PRECISION NOT NULL,
                local_y DOUBLE PRECISION NOT NULL,
                direction_deg DOUBLE PRECISION
            );

            ALTER TABLE spatial_block_instances ADD COLUMN IF NOT EXISTS library_id BIGINT REFERENCES block_library (id);
            CREATE INDEX IF NOT EXISTS idx_spatial_block_instances_library ON spatial_block_instances (library_id);

            -- Seed the default category only into an empty tree, so renaming it never brings the old name back.
            INSERT INTO block_categories (parent_id, name)
            SELECT NULL, '{UncategorizedName}'
            WHERE NOT EXISTS (SELECT 1 FROM block_categories);

            -- Definitions imported from DXF become library items (one per block name, skipping shapes
            -- the library already holds).
            WITH src AS (
                SELECT DISTINCT ON (d.block_name)
                    d.block_name, d.local_geom, d.local_outer_geom
                FROM block_definitions d
                ORDER BY d.block_name, d.id
            ),
            fresh AS (
                SELECT s.* FROM src s
                WHERE NOT EXISTS (SELECT 1 FROM block_library l WHERE l.code = s.block_name)
                  AND NOT EXISTS (
                      SELECT 1 FROM block_library_versions v
                      JOIN block_library l ON l.id = v.library_id AND l.is_deleted = FALSE AND v.version = l.current_version
                      WHERE v.shape_hash = {hashOfCandidate})
            ),
            created AS (
                INSERT INTO block_library (code, name, category_id, width, depth, status)
                SELECT f.block_name, f.block_name,
                    (SELECT min(id) FROM block_categories WHERE parent_id IS NULL),
                    ST_XMax(COALESCE(f.local_outer_geom, f.local_geom)::box3d) - ST_XMin(COALESCE(f.local_outer_geom, f.local_geom)::box3d),
                    ST_YMax(COALESCE(f.local_outer_geom, f.local_geom)::box3d) - ST_YMin(COALESCE(f.local_outer_geom, f.local_geom)::box3d),
                    'released'
                FROM fresh f
                RETURNING id, code
            )
            INSERT INTO block_library_versions (library_id, version, local_geom, local_outer_geom, shape_hash, note)
            SELECT c.id, 1, f.local_geom, f.local_outer_geom, {string.Format(HashOf, "f.local_geom")}, 'Imported from DXF'
            FROM created c JOIN fresh f ON f.block_name = c.code;

            -- Link instances: first by identical shape, then by block name.
            UPDATE spatial_block_instances i SET library_id = v.library_id
            FROM block_definitions d, block_library_versions v, block_library l
            WHERE i.library_id IS NULL AND i.definition_id = d.id
              AND l.id = v.library_id AND l.is_deleted = FALSE AND v.version = l.current_version
              AND v.shape_hash = {hashOfDefinition};

            UPDATE spatial_block_instances i SET library_id = l.id
            FROM block_library l
            WHERE i.library_id IS NULL AND l.is_deleted = FALSE AND l.code = i.block_name;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BlockCategory>> ListCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT id, parent_id, name FROM block_categories ORDER BY name;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<BlockCategory>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new BlockCategory(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2)));
        }

        return result;
    }

    public async Task<long> AddCategoryAsync(long? parentId, string name, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Enter a category name.");
        }

        await using var command = dataSource.CreateCommand(
            "INSERT INTO block_categories (parent_id, name) VALUES ($1, $2) ON CONFLICT DO NOTHING RETURNING id;");
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)parentId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        command.Parameters.AddWithValue(name);
        var id = await command.ExecuteScalarAsync(cancellationToken);
        if (id is null)
        {
            throw new InvalidOperationException($"Category '{name}' already exists here.");
        }

        return (long)id;
    }

    public async Task RenameCategoryAsync(long id, string name, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Enter a category name.");
        }

        try
        {
            await using var command = dataSource.CreateCommand("UPDATE block_categories SET name = $2 WHERE id = $1;");
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(name);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new InvalidOperationException("The category no longer exists.");
            }
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"A category named '{name}' already exists at this level.");
        }
    }

    public async Task<IReadOnlyList<LibraryItem>> ListItemsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT l.id, l.code, l.name, l.category_id, c.name, l.vendor, l.model_no, l.description,
                   l.width, l.depth, l.height, l.status, l.current_version,
                   (SELECT count(*) FROM spatial_block_instances i WHERE i.library_id = l.id),
                   v.local_geom, v.local_outer_geom, l.updated_at
            FROM block_library l
            LEFT JOIN block_categories c ON c.id = l.category_id
            LEFT JOIN block_library_versions v ON v.library_id = l.id AND v.version = l.current_version
            WHERE l.is_deleted = FALSE
            ORDER BY l.code;
            """;

        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<LibraryItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LibraryItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetDouble(9),
                reader.IsDBNull(10) ? null : reader.GetDouble(10),
                reader.GetString(11),
                reader.GetInt32(12),
                (int)reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetFieldValue<Geometry>(14),
                reader.IsDBNull(15) ? null : reader.GetFieldValue<Geometry>(15),
                reader.GetDateTime(16)));
        }

        return result;
    }

    /// <summary>
    /// Registers the shape of a placed block instance as a new library item (version 1) and links
    /// the instance to it. When the library already holds the same shape, the instance is linked
    /// to that item instead of creating a duplicate.
    /// </summary>
    public async Task<LibraryRegisterResult> RegisterFromInstanceAsync(
        long instanceId, string code, string name, long? categoryId, string? vendor, string? modelNo,
        long? userId, CancellationToken cancellationToken = default)
    {
        code = code.Trim();
        name = name.Trim();
        if (code.Length == 0 || name.Length == 0)
        {
            throw new InvalidOperationException("Code and name are required.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The instance's geometry in the block's own coordinate system: the inverse of its
        // translate/rotate/scale transform (same maths as the definition sync).
        const string localGeom = """
            ST_Affine(ST_Translate({0}, -insert_x, -insert_y),
                cos(radians(rotation_deg)) / scale_x, sin(radians(rotation_deg)) / scale_x,
                -sin(radians(rotation_deg)) / scale_y, cos(radians(rotation_deg)) / scale_y, 0, 0)
            """;
        var geomExpr = string.Format(localGeom, "geom");
        var outerExpr = string.Format(localGeom, "outer_geom");

        long? existingLibraryId;
        string? shapeHash;
        double width, depth;
        await using (var probe = new NpgsqlCommand($"""
            SELECT library_id, {string.Format(HashOf, geomExpr)},
                   ST_XMax(({outerExpr})::box3d) - ST_XMin(({outerExpr})::box3d),
                   ST_YMax(({outerExpr})::box3d) - ST_YMin(({outerExpr})::box3d)
            FROM spatial_block_instances
            WHERE id = $1 AND insert_x IS NOT NULL AND insert_y IS NOT NULL AND scale_x <> 0 AND scale_y <> 0;
            """, connection, transaction))
        {
            probe.Parameters.AddWithValue(instanceId);
            await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The block instance was not found or has no placement data.");
            }

            existingLibraryId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            shapeHash = reader.GetString(1);
            width = reader.GetDouble(2);
            depth = reader.GetDouble(3);
        }

        if (existingLibraryId is not null)
        {
            await using var named = new NpgsqlCommand("SELECT code FROM block_library WHERE id = $1;", connection, transaction);
            named.Parameters.AddWithValue(existingLibraryId.Value);
            var existingCode = (string?)await named.ExecuteScalarAsync(cancellationToken);
            throw new InvalidOperationException($"This block is already linked to library item '{existingCode}'.");
        }

        await using (var duplicate = new NpgsqlCommand("""
            SELECT l.id, l.code FROM block_library_versions v
            JOIN block_library l ON l.id = v.library_id AND l.is_deleted = FALSE AND v.version = l.current_version
            WHERE v.shape_hash = $1 LIMIT 1;
            """, connection, transaction))
        {
            duplicate.Parameters.AddWithValue(shapeHash);
            await using var reader = await duplicate.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var existingId = reader.GetInt64(0);
                var existingCode = reader.GetString(1);
                await reader.CloseAsync();
                await LinkInstanceAsync(connection, transaction, instanceId, existingId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new LibraryRegisterResult(LibraryRegisterOutcome.LinkedToExisting, existingId, existingCode);
            }
        }

        await using (var codeCheck = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM block_library WHERE code = $1);", connection, transaction))
        {
            codeCheck.Parameters.AddWithValue(code);
            if ((bool)(await codeCheck.ExecuteScalarAsync(cancellationToken))!)
            {
                throw new InvalidOperationException($"A library item with code '{code}' already exists.");
            }
        }

        long libraryId;
        await using (var insertItem = new NpgsqlCommand("""
            INSERT INTO block_library (code, name, category_id, vendor, model_no, width, depth, status, created_by, updated_by)
            VALUES ($1, $2, COALESCE($3, (SELECT min(id) FROM block_categories WHERE parent_id IS NULL)),
                    $4, $5, $6, $7, 'released', $8, $8)
            RETURNING id;
            """, connection, transaction))
        {
            insertItem.Parameters.AddWithValue(code);
            insertItem.Parameters.AddWithValue(name);
            insertItem.Parameters.Add(new NpgsqlParameter { Value = (object?)categoryId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            insertItem.Parameters.Add(new NpgsqlParameter { Value = (object?)Blank(vendor) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Varchar });
            insertItem.Parameters.Add(new NpgsqlParameter { Value = (object?)Blank(modelNo) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Varchar });
            insertItem.Parameters.AddWithValue(width);
            insertItem.Parameters.AddWithValue(depth);
            insertItem.Parameters.Add(new NpgsqlParameter { Value = (object?)userId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            libraryId = (long)(await insertItem.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var insertVersion = new NpgsqlCommand($"""
            INSERT INTO block_library_versions (library_id, version, local_geom, local_outer_geom, shape_hash, note, created_by)
            SELECT $1, 1, {geomExpr}, {outerExpr}, $2, 'Registered from a placed block', $3
            FROM spatial_block_instances WHERE id = $4;
            """, connection, transaction))
        {
            insertVersion.Parameters.AddWithValue(libraryId);
            insertVersion.Parameters.AddWithValue(shapeHash);
            insertVersion.Parameters.Add(new NpgsqlParameter { Value = (object?)userId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            insertVersion.Parameters.AddWithValue(instanceId);
            await insertVersion.ExecuteNonQueryAsync(cancellationToken);
        }

        // Every instance that shares the source definition is the same block: link them all.
        await using (var linkAll = new NpgsqlCommand("""
            UPDATE spatial_block_instances SET library_id = $1
            WHERE library_id IS NULL
              AND (id = $2 OR (definition_id IS NOT NULL
                   AND definition_id = (SELECT definition_id FROM spatial_block_instances WHERE id = $2)));
            """, connection, transaction))
        {
            linkAll.Parameters.AddWithValue(libraryId);
            linkAll.Parameters.AddWithValue(instanceId);
            await linkAll.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new LibraryRegisterResult(LibraryRegisterOutcome.Created, libraryId, code);
    }

    private static async Task LinkInstanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, long libraryId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE spatial_block_instances SET library_id = $1 WHERE id = $2;", connection, transaction);
        command.Parameters.AddWithValue(libraryId);
        command.Parameters.AddWithValue(instanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateItemAsync(long id, LibraryItemEdit edit, long? userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(edit.Name))
        {
            throw new InvalidOperationException("Enter a name.");
        }

        if (!LibraryStatus.All.Contains(edit.Status))
        {
            throw new InvalidOperationException($"Unknown status '{edit.Status}'.");
        }

        await using var command = dataSource.CreateCommand("""
            UPDATE block_library SET name = $2, category_id = $3, vendor = $4, model_no = $5,
                description = $6, height = $7, status = $8, updated_by = $9, updated_at = CURRENT_TIMESTAMP
            WHERE id = $1 AND is_deleted = FALSE;
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(edit.Name.Trim());
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)edit.CategoryId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)Blank(edit.Vendor) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Varchar });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)Blank(edit.ModelNo) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Varchar });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)Blank(edit.Description) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)edit.Height ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Double });
        command.Parameters.AddWithValue(edit.Status);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)userId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException("The library item no longer exists.");
        }
    }

    /// <summary>Soft-deletes an item. Items still used by placed blocks cannot be deleted (deprecate them instead).</summary>
    public async Task DeleteItemAsync(long id, long? userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var usage = new NpgsqlCommand("SELECT count(*) FROM spatial_block_instances WHERE library_id = $1;", connection);
        usage.Parameters.AddWithValue(id);
        var used = (long)(await usage.ExecuteScalarAsync(cancellationToken))!;
        if (used > 0)
        {
            throw new InvalidOperationException(
                $"{used} placed block(s) still use this item. Set its status to 'deprecated' instead of deleting it.");
        }

        await using var command = new NpgsqlCommand(
            "UPDATE block_library SET is_deleted = TRUE, updated_by = $2, updated_at = CURRENT_TIMESTAMP WHERE id = $1;", connection);
        command.Parameters.AddWithValue(id);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)userId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
