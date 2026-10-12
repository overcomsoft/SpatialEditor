using System.Text.Json;
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using SpatialEditor.Domain;

namespace SpatialEditor.Infrastructure;

/// <summary>
/// Version management of drawings (layouts). A <b>layout</b> owns a working copy (the live
/// <c>spatial_block_instances</c> / <c>spatial_features</c> rows with its <c>layout_id</c>) and any
/// number of saved <b>versions</b>: immutable snapshots in <c>layout_version_blocks</c> and
/// <c>layout_version_features</c>. Objects keep their permanent <c>object_uid</c> across versions, which
/// is what makes comparing two versions meaningful. Everything that changes the working copy runs in one
/// transaction and is announced through the change feed like a normal save.
/// </summary>
public sealed class VersionRepository : IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;

    public VersionRepository(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseNetTopologySuite();
        dataSource = builder.Build();
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    private const string NoLayer = "(no layer)";

    // ------------------------------------------------------------------ schema

    /// <summary>Creates the version tables. Needs <see cref="PostGisFeatureRepository.EnsureSchemaAsync"/> to have run (layouts, object_uid).</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS layout_versions (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                layout_id BIGINT NOT NULL REFERENCES layouts (id) ON DELETE CASCADE,
                version_no INT NOT NULL,
                label VARCHAR(255) NOT NULL,
                comment TEXT,
                parent_version_id BIGINT REFERENCES layout_versions (id) ON DELETE SET NULL,
                status VARCHAR(20) NOT NULL DEFAULT 'draft',
                kind VARCHAR(20) NOT NULL DEFAULT 'manual',
                created_by BIGINT,
                created_by_name VARCHAR(255),
                created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                block_count INT NOT NULL DEFAULT 0,
                feature_count INT NOT NULL DEFAULT 0,
                content_hash VARCHAR(64) NOT NULL DEFAULT '',
                CONSTRAINT uq_layout_versions_no UNIQUE (layout_id, version_no)
            );
            CREATE INDEX IF NOT EXISTS idx_layout_versions_layout ON layout_versions (layout_id, version_no DESC);

            CREATE TABLE IF NOT EXISTS layout_version_blocks (
                version_id BIGINT NOT NULL REFERENCES layout_versions (id) ON DELETE CASCADE,
                object_uid UUID NOT NULL,
                block_name VARCHAR(255) NOT NULL,
                layer_name VARCHAR(255),
                source_file VARCHAR(1024),
                insert_x DOUBLE PRECISION,
                insert_y DOUBLE PRECISION,
                rotation_deg DOUBLE PRECISION NOT NULL DEFAULT 0,
                scale_x DOUBLE PRECISION NOT NULL DEFAULT 1,
                scale_y DOUBLE PRECISION NOT NULL DEFAULT 1,
                attributes JSONB NOT NULL DEFAULT '{}'::jsonb,
                geom GEOMETRY(Geometry, 5186) NOT NULL,
                outer_geom GEOMETRY(Geometry, 5186) NOT NULL,
                PRIMARY KEY (version_id, object_uid)
            );

            CREATE TABLE IF NOT EXISTS layout_version_features (
                version_id BIGINT NOT NULL REFERENCES layout_versions (id) ON DELETE CASCADE,
                object_uid UUID NOT NULL,
                feature_type VARCHAR(50) NOT NULL,
                block_name VARCHAR(255),
                layer_name VARCHAR(255),
                source_file VARCHAR(1024),
                attributes JSONB NOT NULL DEFAULT '{}'::jsonb,
                geom GEOMETRY(Geometry, 5186) NOT NULL,
                outer_geom GEOMETRY(Geometry, 5186),
                PRIMARY KEY (version_id, object_uid)
            );
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ content hash

    // The same expressions run on the working copy and on a snapshot, so equal content gives an equal hash.
    private const string BlockHashExpression = """
        COALESCE(string_agg(concat_ws('|', t.object_uid, t.block_name, t.layer_name,
            round(COALESCE(t.insert_x, 0)::numeric, 4), round(COALESCE(t.insert_y, 0)::numeric, 4),
            round(COALESCE(t.rotation_deg, 0)::numeric, 4), COALESCE(t.scale_x, 1), COALESCE(t.scale_y, 1),
            t.attributes::text, md5(ST_AsBinary(t.geom))), E'\n' ORDER BY t.object_uid), '')
        """;

    private const string FeatureHashExpression = """
        COALESCE(string_agg(concat_ws('|', t.object_uid, t.feature_type, t.block_name, t.layer_name,
            t.attributes::text, md5(ST_AsBinary(t.geom)), md5(COALESCE(ST_AsBinary(t.outer_geom), ''::bytea))), E'\n' ORDER BY t.object_uid), '')
        """;

    private static string WorkingHashSql => $"""
        SELECT md5((SELECT {BlockHashExpression} FROM spatial_block_instances t WHERE t.layout_id = $1)
                || '#' || (SELECT {FeatureHashExpression} FROM spatial_features t WHERE t.layout_id = $1
                           AND t.feature_type IN ('cad_block', 'dxf_layer')));
        """;

    private static string VersionHashSql => $"""
        SELECT md5((SELECT {BlockHashExpression} FROM layout_version_blocks t WHERE t.version_id = $1)
                || '#' || (SELECT {FeatureHashExpression} FROM layout_version_features t WHERE t.version_id = $1));
        """;

    private static async Task<string> WorkingHashAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long layoutId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(WorkingHashSql, connection, transaction);
        command.Parameters.AddWithValue(layoutId);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // ------------------------------------------------------------------ layouts

    public async Task<IReadOnlyList<LayoutInfo>> ListLayoutsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT l.id, l.name, l.description, l.base_version_id, l.created_at, l.created_by_name,
                   (SELECT count(*) FROM spatial_block_instances b WHERE b.layout_id = l.id)
                 + (SELECT count(*) FROM spatial_features f WHERE f.layout_id = l.id AND f.feature_type IN ('cad_block', 'dxf_layer'))
            FROM layouts l
            ORDER BY l.id;
            """;
        await using var command = dataSource.CreateCommand(sql);
        var layouts = new List<LayoutInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            layouts.Add(new LayoutInfo(
                reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                (int)reader.GetInt64(6)));
        }

        return layouts;
    }

    public async Task<long> CreateLayoutAsync(string name, string? description, ChangeContext? context, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO layouts (name, description, created_by_name) VALUES ($1, $2, $3) RETURNING id;");
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue((object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)context?.UserName ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task RenameLayoutAsync(long layoutId, string name, string? description, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("UPDATE layouts SET name = $2, description = $3 WHERE id = $1;");
        command.Parameters.AddWithValue(layoutId);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue((object?)description ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes a layout with its working copy and all its versions. The last remaining layout cannot be
    /// deleted. Returns false when it was refused.
    /// </summary>
    public async Task<bool> DeleteLayoutAsync(long layoutId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM layouts;", connection, transaction))
        {
            if ((long)(await count.ExecuteScalarAsync(cancellationToken))! <= 1)
            {
                return false;
            }
        }

        foreach (var sql in new[]
        {
            "DELETE FROM spatial_block_instances WHERE layout_id = $1;",
            "DELETE FROM spatial_features WHERE layout_id = $1;",
            "DELETE FROM change_log WHERE layout_id = $1;",
            "DELETE FROM layouts WHERE id = $1;"
        })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(layoutId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var definitions = new NpgsqlCommand("""
            DELETE FROM block_definitions d
            WHERE NOT EXISTS (SELECT 1 FROM spatial_block_instances i WHERE i.definition_id = d.id);
            """, connection, transaction))
        {
            await definitions.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Creates a new layout whose working copy is a copy of <paramref name="versionId"/> (a branch):
    /// A/B alternatives can then be edited side by side. Objects keep their uids, so the two layouts can be compared.
    /// </summary>
    public async Task<long> CreateLayoutFromVersionAsync(
        long versionId, string name, string? description, ChangeContext? context, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long layoutId;
        await using (var create = new NpgsqlCommand(
            "INSERT INTO layouts (name, description, base_version_id, created_by_name) VALUES ($1, $2, $3, $4) RETURNING id;", connection, transaction))
        {
            create.Parameters.AddWithValue(name);
            create.Parameters.AddWithValue((object?)description ?? DBNull.Value);
            create.Parameters.AddWithValue(versionId);
            create.Parameters.AddWithValue((object?)context?.UserName ?? DBNull.Value);
            layoutId = (long)(await create.ExecuteScalarAsync(cancellationToken))!;
        }

        await CopyVersionToWorkingCopyAsync(connection, transaction, versionId, layoutId, cancellationToken);
        await PostGisFeatureRepository.SyncBlockDefinitionsAsync(connection, transaction, cancellationToken);
        await LibraryRepository.EnsureSchemaAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return layoutId;
    }

    // ------------------------------------------------------------------ versions

    private const string VersionColumns =
        "v.id, v.layout_id, v.version_no, v.label, v.comment, v.parent_version_id, v.status, v.kind, v.created_by, v.created_by_name, v.created_at, v.block_count, v.feature_count, v.content_hash";

    private static LayoutVersion ReadVersion(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5),
        reader.GetString(6), reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetDateTime(10), reader.GetInt32(11), reader.GetInt32(12), reader.GetString(13));

    public async Task<IReadOnlyList<LayoutVersion>> ListVersionsAsync(long layoutId, bool includeAuto = true, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT {VersionColumns} FROM layout_versions v
            WHERE v.layout_id = $1 {(includeAuto ? string.Empty : "AND v.kind = 'manual'")}
            ORDER BY v.version_no DESC;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(layoutId);
        var versions = new List<LayoutVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(ReadVersion(reader));
        }

        return versions;
    }

    public async Task<LayoutVersion?> GetVersionAsync(long versionId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand($"SELECT {VersionColumns} FROM layout_versions v WHERE v.id = $1;");
        command.Parameters.AddWithValue(versionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadVersion(reader) : null;
    }

    /// <summary>
    /// Saves the working copy of <paramref name="layoutId"/> as a new version. With
    /// <paramref name="skipIfUnchanged"/> nothing is created (null is returned) when the content equals the
    /// newest version's. The snapshot is read in one repeatable-read transaction so a save by another user
    /// in the middle can never produce a half-and-half version.
    /// </summary>
    public async Task<LayoutVersion?> CreateVersionAsync(
        long layoutId, string label, string? comment, string kind, ChangeContext? context,
        bool skipIfUnchanged = true, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var created = await CreateVersionCoreAsync(connection, transaction, layoutId, label, comment, kind, context, skipIfUnchanged, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    private static async Task<LayoutVersion?> CreateVersionCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long layoutId, string label, string? comment, string kind,
        ChangeContext? context, bool skipIfUnchanged, CancellationToken cancellationToken)
    {
        // Serializes version numbering of one layout across sessions.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1);", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue(layoutId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var hash = await WorkingHashAsync(connection, transaction, layoutId, cancellationToken);
        long? parentId;
        int nextNo;
        string? newestHash = null;
        await using (var head = new NpgsqlCommand("""
            SELECT l.base_version_id,
                   COALESCE((SELECT max(version_no) FROM layout_versions WHERE layout_id = $1), 0) + 1,
                   (SELECT content_hash FROM layout_versions WHERE layout_id = $1 ORDER BY version_no DESC LIMIT 1)
            FROM layouts l WHERE l.id = $1;
            """, connection, transaction))
        {
            head.Parameters.AddWithValue(layoutId);
            await using var reader = await head.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"Layout {layoutId} does not exist.");
            }

            parentId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            nextNo = reader.GetInt32(1);
            newestHash = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        if (skipIfUnchanged && string.Equals(newestHash, hash, StringComparison.Ordinal))
        {
            return null;
        }

        long versionId;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO layout_versions (layout_id, version_no, label, comment, parent_version_id, kind, created_by, created_by_name, content_hash)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            RETURNING id;
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(layoutId);
            insert.Parameters.AddWithValue(nextNo);
            insert.Parameters.AddWithValue(label);
            insert.Parameters.AddWithValue((object?)comment ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)parentId ?? DBNull.Value);
            insert.Parameters.AddWithValue(kind);
            insert.Parameters.AddWithValue((object?)context?.UserId ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)context?.UserName ?? DBNull.Value);
            insert.Parameters.AddWithValue(hash);
            versionId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var blocks = new NpgsqlCommand("""
            INSERT INTO layout_version_blocks
                (version_id, object_uid, block_name, layer_name, source_file, insert_x, insert_y, rotation_deg, scale_x, scale_y, attributes, geom, outer_geom)
            SELECT $2, object_uid, block_name, layer_name, source_file, insert_x, insert_y,
                   COALESCE(rotation_deg, 0), COALESCE(scale_x, 1), COALESCE(scale_y, 1), attributes, geom, outer_geom
            FROM spatial_block_instances WHERE layout_id = $1;
            """, connection, transaction))
        {
            blocks.Parameters.AddWithValue(layoutId);
            blocks.Parameters.AddWithValue(versionId);
            await blocks.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var features = new NpgsqlCommand("""
            INSERT INTO layout_version_features
                (version_id, object_uid, feature_type, block_name, layer_name, source_file, attributes, geom, outer_geom)
            SELECT $2, object_uid, feature_type, block_name, layer_name, source_file, attributes, geom, outer_geom
            FROM spatial_features WHERE layout_id = $1 AND feature_type IN ('cad_block', 'dxf_layer');
            """, connection, transaction))
        {
            features.Parameters.AddWithValue(layoutId);
            features.Parameters.AddWithValue(versionId);
            await features.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var counts = new NpgsqlCommand("""
            UPDATE layout_versions SET
                block_count = (SELECT count(*) FROM layout_version_blocks WHERE version_id = $1),
                feature_count = (SELECT count(*) FROM layout_version_features WHERE version_id = $1)
            WHERE id = $1;
            """, connection, transaction))
        {
            counts.Parameters.AddWithValue(versionId);
            await counts.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var move = new NpgsqlCommand("UPDATE layouts SET base_version_id = $2 WHERE id = $1;", connection, transaction))
        {
            move.Parameters.AddWithValue(layoutId);
            move.Parameters.AddWithValue(versionId);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var read = new NpgsqlCommand($"SELECT {VersionColumns} FROM layout_versions v WHERE v.id = $1;", connection, transaction);
        read.Parameters.AddWithValue(versionId);
        await using var created = await read.ExecuteReaderAsync(cancellationToken);
        await created.ReadAsync(cancellationToken);
        return ReadVersion(created);
    }

    /// <summary>Where the working copy stands: which version it started from and whether it was changed since.</summary>
    public async Task<LayoutStatus?> GetStatusAsync(long layoutId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        string name;
        long? baseId;
        await using (var command = new NpgsqlCommand("SELECT name, base_version_id FROM layouts WHERE id = $1;", connection))
        {
            command.Parameters.AddWithValue(layoutId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            name = reader.GetString(0);
            baseId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        if (baseId is null)
        {
            return new LayoutStatus(layoutId, name, null, null, true);
        }

        int? baseNo = null;
        string? baseLabel = null;
        string? baseHash = null;
        await using (var command = new NpgsqlCommand("SELECT version_no, label, content_hash FROM layout_versions WHERE id = $1;", connection))
        {
            command.Parameters.AddWithValue(baseId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                baseNo = reader.GetInt32(0);
                baseLabel = reader.GetString(1);
                baseHash = reader.GetString(2);
            }
        }

        var working = await WorkingHashAsync(connection, null, layoutId, cancellationToken);
        return new LayoutStatus(layoutId, name, baseNo, baseLabel, baseHash is null || !string.Equals(working, baseHash, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ items / diff

    private static IReadOnlyDictionary<string, string?> ReadAttributes(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? new Dictionary<string, string?>()
            : JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(ordinal)) ?? new Dictionary<string, string?>();

    private async Task<List<VersionBlockItem>> ReadBlocksAsync(string sql, long id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(id);
        var items = new List<VersionBlockItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new VersionBlockItem(
                reader.GetGuid(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                reader.IsDBNull(7) ? 1 : reader.GetDouble(7),
                reader.IsDBNull(8) ? 1 : reader.GetDouble(8),
                ReadAttributes(reader, 9),
                reader.GetFieldValue<Geometry>(10),
                reader.GetFieldValue<Geometry>(11)));
        }

        return items;
    }

    private async Task<List<VersionFeatureItem>> ReadFeaturesAsync(string sql, long id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(id);
        var items = new List<VersionFeatureItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new VersionFeatureItem(
                reader.GetGuid(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ReadAttributes(reader, 5),
                reader.GetFieldValue<Geometry>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<Geometry>(7)));
        }

        return items;
    }

    private const string VersionBlocksSql = """
        SELECT object_uid, block_name, layer_name, source_file, insert_x, insert_y, rotation_deg, scale_x, scale_y, attributes, geom, outer_geom
        FROM layout_version_blocks WHERE version_id = $1 ORDER BY block_name, object_uid;
        """;

    private const string WorkingBlocksSql = """
        SELECT object_uid, block_name, layer_name, source_file, insert_x, insert_y, rotation_deg, scale_x, scale_y, attributes, geom, outer_geom
        FROM spatial_block_instances WHERE layout_id = $1 ORDER BY block_name, object_uid;
        """;

    private const string VersionFeaturesSql = """
        SELECT object_uid, feature_type, block_name, layer_name, source_file, attributes, geom, outer_geom
        FROM layout_version_features WHERE version_id = $1 ORDER BY feature_type, object_uid;
        """;

    private const string WorkingFeaturesSql = """
        SELECT object_uid, feature_type, block_name, layer_name, source_file, attributes, geom, outer_geom
        FROM spatial_features WHERE layout_id = $1 AND feature_type IN ('cad_block', 'dxf_layer') ORDER BY feature_type, object_uid;
        """;

    public Task<List<VersionBlockItem>> GetVersionBlocksAsync(long versionId, CancellationToken cancellationToken = default) =>
        ReadBlocksAsync(VersionBlocksSql, versionId, cancellationToken);

    public Task<List<VersionFeatureItem>> GetVersionFeaturesAsync(long versionId, CancellationToken cancellationToken = default) =>
        ReadFeaturesAsync(VersionFeaturesSql, versionId, cancellationToken);

    public Task<List<VersionBlockItem>> GetWorkingBlocksAsync(long layoutId, CancellationToken cancellationToken = default) =>
        ReadBlocksAsync(WorkingBlocksSql, layoutId, cancellationToken);

    /// <summary>
    /// Compares two states. <paramref name="fromVersionId"/> / <paramref name="toVersionId"/> null means "the
    /// working copy of <paramref name="layoutId"/>". The result describes what changed going from "from" to "to".
    /// </summary>
    public async Task<VersionDiff> DiffAsync(long layoutId, long? fromVersionId, long? toVersionId, CancellationToken cancellationToken = default)
    {
        var fromBlocks = fromVersionId is { } f ? ReadBlocksAsync(VersionBlocksSql, f, cancellationToken) : ReadBlocksAsync(WorkingBlocksSql, layoutId, cancellationToken);
        var toBlocks = toVersionId is { } t ? ReadBlocksAsync(VersionBlocksSql, t, cancellationToken) : ReadBlocksAsync(WorkingBlocksSql, layoutId, cancellationToken);
        var fromFeatures = fromVersionId is { } f2 ? ReadFeaturesAsync(VersionFeaturesSql, f2, cancellationToken) : ReadFeaturesAsync(WorkingFeaturesSql, layoutId, cancellationToken);
        var toFeatures = toVersionId is { } t2 ? ReadFeaturesAsync(VersionFeaturesSql, t2, cancellationToken) : ReadFeaturesAsync(WorkingFeaturesSql, layoutId, cancellationToken);
        await Task.WhenAll(fromBlocks, toBlocks, fromFeatures, toFeatures);
        return VersionDiffer.Compare(fromBlocks.Result, toBlocks.Result, fromFeatures.Result, toFeatures.Result);
    }

    // ------------------------------------------------------------------ restore / branch

    private static async Task CopyVersionToWorkingCopyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long versionId, long layoutId, CancellationToken cancellationToken)
    {
        await using (var blocks = new NpgsqlCommand("""
            INSERT INTO spatial_block_instances
                (source_file, block_name, layer_name, attributes, geom, outer_geom, layout_id, object_uid, insert_x, insert_y, rotation_deg, scale_x, scale_y)
            SELECT COALESCE(source_file, 'version'), block_name, layer_name, attributes, geom, outer_geom, $2, object_uid, insert_x, insert_y, rotation_deg, scale_x, scale_y
            FROM layout_version_blocks WHERE version_id = $1;
            """, connection, transaction))
        {
            blocks.Parameters.AddWithValue(versionId);
            blocks.Parameters.AddWithValue(layoutId);
            await blocks.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var features = new NpgsqlCommand("""
            INSERT INTO spatial_features
                (feature_type, block_name, source_file, layer_name, attributes, geom, outer_geom, layout_id, object_uid)
            SELECT feature_type, block_name, source_file, layer_name, attributes, geom, outer_geom, $2, object_uid
            FROM layout_version_features WHERE version_id = $1;
            """, connection, transaction);
        features.Parameters.AddWithValue(versionId);
        features.Parameters.AddWithValue(layoutId);
        await features.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the working copy of <paramref name="layoutId"/> with <paramref name="versionId"/>. When the working
    /// copy differs from the newest version it is first saved as a "pre-restore" version, so a restore can itself
    /// be undone. Returns the pre-restore version (or null when nothing needed saving).
    /// </summary>
    public async Task<LayoutVersion?> RestoreVersionAsync(
        long layoutId, long versionId, ChangeContext? context, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string label;
        int versionNo;
        await using (var info = new NpgsqlCommand("SELECT label, version_no FROM layout_versions WHERE id = $1;", connection, transaction))
        {
            info.Parameters.AddWithValue(versionId);
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The version no longer exists.");
            }

            label = reader.GetString(0);
            versionNo = reader.GetInt32(1);
        }

        var safety = await CreateVersionCoreAsync(
            connection, transaction, layoutId, $"복원 전 자동 저장 (v{versionNo} 복원)", null, VersionKind.PreRestore, context,
            skipIfUnchanged: true, cancellationToken);

        // Layers before and after: every one of them changes for other sessions.
        var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var layerQuery = new NpgsqlCommand("""
            SELECT COALESCE(layer_name, '(no layer)') FROM spatial_block_instances WHERE layout_id = $1
            UNION SELECT COALESCE(layer_name, '(no layer)') FROM spatial_features WHERE layout_id = $1
            UNION SELECT COALESCE(layer_name, '(no layer)') FROM layout_version_blocks WHERE version_id = $2
            UNION SELECT COALESCE(layer_name, '(no layer)') FROM layout_version_features WHERE version_id = $2;
            """, connection, transaction))
        {
            layerQuery.Parameters.AddWithValue(layoutId);
            layerQuery.Parameters.AddWithValue(versionId);
            await using var reader = await layerQuery.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                layers.Add(reader.GetString(0));
            }
        }

        foreach (var sql in new[]
        {
            "DELETE FROM spatial_block_instances WHERE layout_id = $1;",
            "DELETE FROM spatial_features WHERE layout_id = $1 AND feature_type IN ('cad_block', 'dxf_layer');"
        })
        {
            await using var delete = new NpgsqlCommand(sql, connection, transaction);
            delete.Parameters.AddWithValue(layoutId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await CopyVersionToWorkingCopyAsync(connection, transaction, versionId, layoutId, cancellationToken);
        await using (var baseUpdate = new NpgsqlCommand("UPDATE layouts SET base_version_id = $2 WHERE id = $1;", connection, transaction))
        {
            baseUpdate.Parameters.AddWithValue(layoutId);
            baseUpdate.Parameters.AddWithValue(versionId);
            await baseUpdate.ExecuteNonQueryAsync(cancellationToken);
        }

        await PostGisFeatureRepository.SyncBlockDefinitionsAsync(connection, transaction, cancellationToken);
        await LibraryRepository.EnsureSchemaAsync(connection, transaction, cancellationToken);
        await ChangeFeedRepository.RecordAsync(
            connection, transaction, context,
            layers.Select(name => new ChangeEntry(name, ChangeEntityType.Import, null, ChangeOperation.Update)).ToList(),
            cancellationToken, layoutId);
        await transaction.CommitAsync(cancellationToken);
        _ = label;
        return safety;
    }

    // ------------------------------------------------------------------ status / edit / delete

    public async Task UpdateVersionAsync(long versionId, string label, string? comment, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE layout_versions SET label = $2, comment = $3
            WHERE id = $1 AND status <> 'baseline';
            """);
        command.Parameters.AddWithValue(versionId);
        command.Parameters.AddWithValue(label);
        command.Parameters.AddWithValue((object?)comment ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException("A baseline version cannot be edited. Release the baseline first.");
        }
    }

    /// <summary>
    /// Sets a version's status. Only one baseline per layout: making a version the baseline releases the previous one.
    /// </summary>
    public async Task SetStatusAsync(long versionId, string status, CancellationToken cancellationToken = default)
    {
        if (status is not (VersionStatus.Draft or VersionStatus.Released or VersionStatus.Baseline or VersionStatus.Archived))
        {
            throw new ArgumentException("Unknown status.", nameof(status));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (status == VersionStatus.Baseline)
        {
            await using var release = new NpgsqlCommand("""
                UPDATE layout_versions SET status = 'released'
                WHERE status = 'baseline' AND id <> $1 AND layout_id = (SELECT layout_id FROM layout_versions WHERE id = $1);
                """, connection, transaction);
            release.Parameters.AddWithValue(versionId);
            await release.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var update = new NpgsqlCommand("UPDATE layout_versions SET status = $2 WHERE id = $1;", connection, transaction);
        update.Parameters.AddWithValue(versionId);
        update.Parameters.AddWithValue(status);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes a version. A baseline version is protected (release it first). Children are re-attached to the
    /// deleted version's parent so the history stays connected.
    /// </summary>
    public async Task DeleteVersionAsync(long versionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string status;
        long? parent;
        await using (var info = new NpgsqlCommand("SELECT status, parent_version_id FROM layout_versions WHERE id = $1 FOR UPDATE;", connection, transaction))
        {
            info.Parameters.AddWithValue(versionId);
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            status = reader.GetString(0);
            parent = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        if (status == VersionStatus.Baseline)
        {
            throw new InvalidOperationException("A baseline version cannot be deleted. Release the baseline first.");
        }

        foreach (var sql in new[]
        {
            "UPDATE layout_versions SET parent_version_id = $2 WHERE parent_version_id = $1;",
            "UPDATE layouts SET base_version_id = $2 WHERE base_version_id = $1;",
            "DELETE FROM layout_versions WHERE id = $1;"
        })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(versionId);
            if (sql.Contains("$2"))
            {
                command.Parameters.AddWithValue((object?)parent ?? DBNull.Value);
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

}
