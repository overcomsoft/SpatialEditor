using System.Text;
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using SpatialEditor.Domain;

namespace SpatialEditor.Infrastructure;

public sealed class PostGisFeatureRepository : IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;

    public PostGisFeatureRepository(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseNetTopologySuite();
        dataSource = builder.Build();
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT current_database(), current_user, PostGIS_Version();",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("PostgreSQL connection returned no server information.");
        }

        return $"Connected: database={reader.GetString(0)}, user={reader.GetString(1)}, PostGIS={reader.GetString(2)}";
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE EXTENSION IF NOT EXISTS postgis;

            CREATE TABLE IF NOT EXISTS spatial_features (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                feature_type VARCHAR(32) NOT NULL,
                block_name VARCHAR(255),
                source_file VARCHAR(1024),
                layer_name VARCHAR(255),
                attributes JSONB NOT NULL DEFAULT '{}'::jsonb,
                geom GEOMETRY(Geometry, 5186) NOT NULL,
                outer_geom GEOMETRY(Geometry, 5186),
                created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS spatial_block_instances (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                source_file VARCHAR(1024) NOT NULL,
                block_name VARCHAR(255) NOT NULL,
                layer_name VARCHAR(255),
                attributes JSONB NOT NULL DEFAULT '{}'::jsonb,
                geom GEOMETRY(Geometry, 5186) NOT NULL,
                outer_geom GEOMETRY(Geometry, 5186) NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            ALTER TABLE spatial_block_instances ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP;
            ALTER TABLE spatial_features ADD COLUMN IF NOT EXISTS block_name VARCHAR(255);
            ALTER TABLE spatial_features ADD COLUMN IF NOT EXISTS source_file VARCHAR(1024);
            ALTER TABLE spatial_features ADD COLUMN IF NOT EXISTS layer_name VARCHAR(255);
            ALTER TABLE spatial_features ADD COLUMN IF NOT EXISTS outer_geom GEOMETRY(Polygon, 5186);
            ALTER TABLE spatial_features ALTER COLUMN outer_geom TYPE GEOMETRY(Geometry, 5186) USING outer_geom::geometry;

            CREATE TABLE IF NOT EXISTS block_definitions (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                source_file VARCHAR(1024) NOT NULL,
                block_name VARCHAR(255) NOT NULL,
                local_geom GEOMETRY(Geometry, 5186) NOT NULL,
                local_outer_geom GEOMETRY(Geometry, 5186),
                created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT block_definitions_unique UNIQUE (source_file, block_name)
            );

            ALTER TABLE spatial_block_instances ADD COLUMN IF NOT EXISTS definition_id BIGINT REFERENCES block_definitions (id);
            ALTER TABLE spatial_block_instances ADD COLUMN IF NOT EXISTS insert_x DOUBLE PRECISION;
            ALTER TABLE spatial_block_instances ADD COLUMN IF NOT EXISTS insert_y DOUBLE PRECISION;
            ALTER TABLE spatial_block_instances ADD COLUMN IF NOT EXISTS rotation_deg DOUBLE PRECISION;
            ALTER TABLE spatial_block_instances ADD COLUMN IF NOT EXISTS scale_x DOUBLE PRECISION;
            ALTER TABLE spatial_block_instances ADD COLUMN IF NOT EXISTS scale_y DOUBLE PRECISION;

            CREATE INDEX IF NOT EXISTS idx_spatial_features_geom ON spatial_features USING GIST (geom);
            CREATE INDEX IF NOT EXISTS idx_spatial_features_outer_geom ON spatial_features USING GIST (outer_geom);
            CREATE INDEX IF NOT EXISTS idx_spatial_features_attributes ON spatial_features USING GIN (attributes);
            CREATE INDEX IF NOT EXISTS idx_spatial_block_instances_geom ON spatial_block_instances USING GIST (geom);
            CREATE INDEX IF NOT EXISTS idx_spatial_block_instances_outer_geom ON spatial_block_instances USING GIST (outer_geom);
            CREATE INDEX IF NOT EXISTS idx_spatial_block_instances_layer ON spatial_block_instances (layer_name);
            CREATE INDEX IF NOT EXISTS idx_spatial_block_instances_definition ON spatial_block_instances (definition_id);
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Backfills definitions/transform columns for instances imported before they existed.
        await SyncBlockDefinitionsAsync(connection, null, cancellationToken);
    }

    /// <summary>
    /// Parses a numeric attribute out of the instance's JSONB, yielding NULL for missing or
    /// malformed values so one bad row can never abort the whole sync.
    /// </summary>
    private static string NumericAttribute(string key) =>
        $"CASE WHEN attributes->>'{key}' ~ '^[-+]?([0-9]+\\.?[0-9]*|\\.[0-9]+)([eE][-+]?[0-9]+)?$' THEN (attributes->>'{key}')::double precision END";

    /// <summary>
    /// Keeps the block model consistent: (1) mirrors the placement attributes
    /// (block_insert_x/y, block_rotation_deg, block_scale_x/y) into real transform columns,
    /// (2) creates one <c>block_definitions</c> row per (source_file, block_name) holding the
    /// block's geometry in its own local coordinate system — the inverse of an instance's
    /// transform applied to its world geometry, preferring an instance that was never edited —
    /// and (3) links every instance to its definition. Idempotent; scoped to rows that changed.
    /// </summary>
    private static async Task SyncBlockDefinitionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE spatial_block_instances SET
                insert_x = {NumericAttribute("block_insert_x")},
                insert_y = {NumericAttribute("block_insert_y")},
                rotation_deg = COALESCE({NumericAttribute("block_rotation_deg")}, 0),
                scale_x = COALESCE({NumericAttribute("block_scale_x")}, 1),
                scale_y = COALESCE({NumericAttribute("block_scale_y")}, 1)
            WHERE (insert_x, insert_y, rotation_deg, scale_x, scale_y) IS DISTINCT FROM (
                {NumericAttribute("block_insert_x")},
                {NumericAttribute("block_insert_y")},
                COALESCE({NumericAttribute("block_rotation_deg")}, 0),
                COALESCE({NumericAttribute("block_scale_x")}, 1),
                COALESCE({NumericAttribute("block_scale_y")}, 1));

            INSERT INTO block_definitions (source_file, block_name, local_geom, local_outer_geom)
            SELECT DISTINCT ON (source_file, block_name)
                source_file, block_name,
                ST_Affine(ST_Translate(geom, -insert_x, -insert_y),
                    cos(radians(rotation_deg)) / scale_x, sin(radians(rotation_deg)) / scale_x,
                    -sin(radians(rotation_deg)) / scale_y, cos(radians(rotation_deg)) / scale_y, 0, 0),
                ST_Affine(ST_Translate(outer_geom, -insert_x, -insert_y),
                    cos(radians(rotation_deg)) / scale_x, sin(radians(rotation_deg)) / scale_x,
                    -sin(radians(rotation_deg)) / scale_y, cos(radians(rotation_deg)) / scale_y, 0, 0)
            FROM spatial_block_instances
            WHERE definition_id IS NULL
              AND insert_x IS NOT NULL AND insert_y IS NOT NULL
              AND scale_x <> 0 AND scale_y <> 0
            ORDER BY source_file, block_name, (updated_at = created_at) DESC, id
            ON CONFLICT (source_file, block_name) DO NOTHING;

            UPDATE spatial_block_instances i SET definition_id = d.id
            FROM block_definitions d
            WHERE i.definition_id IS NULL
              AND d.source_file = i.source_file AND d.block_name = i.block_name;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SpatialFeature>> FindByBoundsAsync(
        Envelope bounds,
        int srid = 5186,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, feature_type, geom, attributes
            FROM spatial_features
            WHERE geom && ST_MakeEnvelope($1, $2, $3, $4, $5)
            ORDER BY id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(bounds.MinX);
        command.Parameters.AddWithValue(bounds.MinY);
        command.Parameters.AddWithValue(bounds.MaxX);
        command.Parameters.AddWithValue(bounds.MaxY);
        command.Parameters.AddWithValue(srid);

        var features = new List<SpatialFeature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var attributes = reader.IsDBNull(3)
                ? new Dictionary<string, string?>()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(3))
                    ?? new Dictionary<string, string?>();
            features.Add(new SpatialFeature(reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<Geometry>(2), attributes));
        }

        return features;
    }

    public async Task<IReadOnlyList<SpatialFeature>> FindImportedOuterPolygonsAsync(
        IReadOnlyCollection<string>? layerNames = null,
        CancellationToken cancellationToken = default)
    {
        var sql = """
                        SELECT f.id, f.feature_type, f.layer_name, dumped.geom, f.attributes
                        FROM spatial_features f
                        CROSS JOIN LATERAL ST_Dump(f.outer_geom) AS dumped
                        WHERE f.outer_geom IS NOT NULL
                            AND f.feature_type IN ('cad_block', 'dxf_layer')
            """;
        if (layerNames is { Count: > 0 })
        {
            sql += " AND COALESCE(f.layer_name, '(no layer)') = ANY($1)";
        }

        sql += " ORDER BY id;";

        await using var command = dataSource.CreateCommand(sql);
        if (layerNames is { Count: > 0 })
        {
            command.Parameters.AddWithValue(layerNames.ToArray());
        }

        var features = new List<SpatialFeature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var attributes = reader.IsDBNull(4)
                ? new Dictionary<string, string?>()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(4))
                    ?? new Dictionary<string, string?>();
            features.Add(new SpatialFeature(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetFieldValue<Geometry>(3),
                attributes,
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return features;
    }

    public async Task<IReadOnlyList<SpatialFeature>> FindImportedBlockInstanceOutersAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT i.id, 'block_instance', i.layer_name, dumped.geom, i.attributes
            FROM spatial_block_instances i
            CROSS JOIN LATERAL ST_Dump(i.outer_geom) AS dumped
            WHERE i.outer_geom IS NOT NULL
            ORDER BY i.id, dumped.path;
            """;

        await using var command = dataSource.CreateCommand(sql);
        var features = new List<SpatialFeature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var attributes = reader.IsDBNull(4)
                ? new Dictionary<string, string?>()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(4))
                    ?? new Dictionary<string, string?>();
            features.Add(new SpatialFeature(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetFieldValue<Geometry>(3),
                attributes,
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return features;
    }

    public async Task<IReadOnlyList<SpatialFeature>> FindImportedBlockInstanceGeometriesAsync(
        IReadOnlyCollection<string>? layerNames = null,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT i.id, 'block_instance', i.layer_name, dumped.geom, i.attributes
            FROM spatial_block_instances i
            CROSS JOIN LATERAL ST_Dump(i.geom) AS dumped
            WHERE i.geom IS NOT NULL
            """;
        if (layerNames is { Count: > 0 })
        {
            sql += " AND COALESCE(i.layer_name, '(no layer)') = ANY($1)";
        }

        sql += " ORDER BY i.id, dumped.path;";

        await using var command = dataSource.CreateCommand(sql);
        if (layerNames is { Count: > 0 })
        {
            command.Parameters.AddWithValue(layerNames.ToArray());
        }

        var features = new List<SpatialFeature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var attributes = reader.IsDBNull(4)
                ? new Dictionary<string, string?>()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(4))
                    ?? new Dictionary<string, string?>();
            features.Add(new SpatialFeature(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetFieldValue<Geometry>(3),
                attributes,
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return features;
    }

    public async Task<IReadOnlyList<LayerInfo>> FindImportedLayersAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH layer_names AS (
                SELECT DISTINCT COALESCE(layer_name, '(no layer)') AS layer_name
                FROM spatial_features
                WHERE feature_type IN ('cad_block', 'dxf_layer')
                UNION
                SELECT DISTINCT COALESCE(layer_name, '(no layer)') AS layer_name
                FROM spatial_block_instances
            ),
            entity_types AS (
                SELECT COALESCE(f.layer_name, '(no layer)') AS layer_name,
                       string_agg(DISTINCT CASE ST_GeometryType(d.geom)
                       WHEN 'ST_Point' THEN 'Point'
                       WHEN 'ST_LineString' THEN 'Line'
                       WHEN 'ST_Polygon' THEN 'Polygon'
                       ELSE ST_GeometryType(d.geom)
                       END, ', ' ORDER BY CASE ST_GeometryType(d.geom)
                       WHEN 'ST_Point' THEN 'Point'
                       WHEN 'ST_LineString' THEN 'Line'
                       WHEN 'ST_Polygon' THEN 'Polygon'
                       ELSE ST_GeometryType(d.geom)
                       END) AS geometry_types,
                       COUNT(*) AS entity_count
                FROM spatial_features f
                CROSS JOIN LATERAL ST_Dump(f.geom) d
                WHERE f.feature_type IN ('cad_block', 'dxf_layer')
                GROUP BY COALESCE(f.layer_name, '(no layer)')
            ),
            instance_counts AS (
                SELECT COALESCE(layer_name, '(no layer)') AS layer_name,
                       COUNT(*) AS instance_count
                FROM spatial_block_instances
                GROUP BY COALESCE(layer_name, '(no layer)')
            )
            SELECT names.layer_name,
                   COALESCE(types.geometry_types, 'Block') AS geometry_types,
                   COALESCE(types.entity_count, 0) AS entity_count,
                   COALESCE(instances.instance_count, 0) AS instance_count
            FROM layer_names names
            LEFT JOIN entity_types types ON types.layer_name = names.layer_name
            LEFT JOIN instance_counts instances ON instances.layer_name = names.layer_name
            ORDER BY names.layer_name;
            """;

        await using var command = dataSource.CreateCommand(sql);
        var layers = new List<LayerInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            layers.Add(new LayerInfo(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
        }

        return layers;
    }

    public async Task DeleteImportedFileAsync(string sourceFile, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM spatial_features
            WHERE source_file = $1
              AND feature_type IN ('cad_block', 'dxf_layer');
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(sourceFile);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var instanceCommand = new NpgsqlCommand(
            "DELETE FROM spatial_block_instances WHERE source_file = $1;", connection);
        instanceCommand.Parameters.AddWithValue(sourceFile);
        await instanceCommand.ExecuteNonQueryAsync(cancellationToken);

        // Instances reference their definitions, so definitions go last.
        await using var definitionCommand = new NpgsqlCommand(
            "DELETE FROM block_definitions WHERE source_file = $1;", connection);
        definitionCommand.Parameters.AddWithValue(sourceFile);
        await definitionCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasImportedFileAsync(string sourceFile, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM spatial_features
                WHERE source_file = $1 AND feature_type IN ('cad_block', 'dxf_layer')
            ) OR EXISTS (
                SELECT 1 FROM spatial_block_instances
                WHERE source_file = $1
            );
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(sourceFile);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<IReadOnlyList<ImportLayerCount>> FindImportedCountsAsync(
        string sourceFile,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH entity_counts AS (
                SELECT COALESCE(layer_name, '(no layer)') AS layer_name, COUNT(*) AS entity_count
                FROM spatial_features f
                CROSS JOIN LATERAL ST_Dump(f.geom) d
                WHERE f.source_file = $1
                  AND f.feature_type IN ('cad_block', 'dxf_layer')
                GROUP BY COALESCE(layer_name, '(no layer)')
            ),
            block_counts AS (
                SELECT COALESCE(layer_name, '(no layer)') AS layer_name, COUNT(*) AS block_count
                FROM spatial_block_instances
                WHERE source_file = $1
                GROUP BY COALESCE(layer_name, '(no layer)')
            )
            SELECT COALESCE(e.layer_name, b.layer_name),
                   COALESCE(e.entity_count, 0),
                   COALESCE(b.block_count, 0)
            FROM entity_counts e
            FULL OUTER JOIN block_counts b ON b.layer_name = e.layer_name
            ORDER BY 1;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(sourceFile);
        var counts = new List<ImportLayerCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new ImportLayerCount(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return counts;
    }

    public async Task<IReadOnlyList<EditableLayerObject>> FindEditableLayerObjectsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, feature_type, block_name, source_file, layer_name, geom, outer_geom, attributes, updated_at
            FROM spatial_features
            WHERE feature_type IN ('cad_block', 'dxf_layer')
            ORDER BY id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        var objects = new List<EditableLayerObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var attributes = reader.IsDBNull(7)
                ? new Dictionary<string, string?>()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(7))
                    ?? new Dictionary<string, string?>();
            objects.Add(new EditableLayerObject(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetFieldValue<Geometry>(5),
                reader.GetFieldValue<Geometry>(6),
                attributes,
                reader.GetDateTime(8)));
        }

        return objects;
    }

    /// <summary>
    /// Block instances (e.g. Equipment) already store both their actual drawn shape (<c>geom</c>)
    /// and a bounding outline (<c>outer_geom</c>) in the same row, so loading them as
    /// <see cref="EditableLayerObject"/>s (tagged <c>block_instance</c>) lets edit mode move/rotate
    /// the real shape and its outline together instead of only the outline.
    /// </summary>
    public async Task<IReadOnlyList<EditableLayerObject>> FindEditableBlockInstancesAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, block_name, source_file, layer_name, geom, outer_geom, attributes, updated_at
            FROM spatial_block_instances
            ORDER BY id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        var objects = new List<EditableLayerObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var attributes = reader.IsDBNull(6)
                ? new Dictionary<string, string?>()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(6))
                    ?? new Dictionary<string, string?>();
            objects.Add(new EditableLayerObject(
                reader.GetInt64(0),
                "block_instance",
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<Geometry>(4),
                reader.GetFieldValue<Geometry>(5),
                attributes,
                reader.GetDateTime(7)));
        }

        return objects;
    }

    /// <summary>
    /// Applies pending layer edits in one transaction. Updates/deletes are guarded by the
    /// <c>updated_at</c> value read when the object entered edit mode (optimistic concurrency):
    /// if another session already changed a row, its statement affects zero rows and the id is
    /// reported back as a conflict instead of being silently overwritten. Non-conflicting
    /// updates/deletes/inserts in the same batch still commit.
    /// </summary>
    public async Task<IReadOnlyList<long>> ApplyLayerEditsAsync(
        IReadOnlyList<(long Id, Geometry Geometry, Geometry OuterGeometry, DateTime ExpectedUpdatedAt, bool IsBlockInstance, IReadOnlyDictionary<string, string?>? Attributes)> updates,
        IReadOnlyList<(EditableLayerObject Source, Geometry Geometry, Geometry OuterGeometry)> inserts,
        IReadOnlyList<(long Id, DateTime ExpectedUpdatedAt, bool IsBlockInstance)> deletes,
        CancellationToken cancellationToken = default)
    {
        var conflictIds = new List<long>();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var update in updates)
        {
            var table = update.IsBlockInstance ? "spatial_block_instances" : "spatial_features";
            // Block instances also persist their placement attributes (insert point / rotation),
            // so the stored attributes keep matching the moved geometry.
            var writeAttributes = update.Attributes is not null;
            await using var command = new NpgsqlCommand(
                writeAttributes
                    ? $"UPDATE {table} SET geom = $1, outer_geom = $2, attributes = $5, updated_at = CURRENT_TIMESTAMP WHERE id = $3 AND updated_at = $4;"
                    : $"UPDATE {table} SET geom = $1, outer_geom = $2, updated_at = CURRENT_TIMESTAMP WHERE id = $3 AND updated_at = $4;",
                connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = update.Geometry });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = update.OuterGeometry });
            command.Parameters.AddWithValue(update.Id);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = update.ExpectedUpdatedAt });
            if (writeAttributes)
            {
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Json.JsonSerializer.Serialize(update.Attributes) });
            }
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                conflictIds.Add(update.Id);
            }
        }

        foreach (var insert in inserts)
        {
            if (insert.Source.FeatureType == "block_instance")
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO spatial_block_instances
                        (source_file, block_name, layer_name, attributes, geom, outer_geom)
                    VALUES
                        ($1, $2, $3, $4, $5, $6);
                    """, connection, transaction);
                command.Parameters.AddWithValue((object?)insert.Source.SourceFile ?? DBNull.Value);
                command.Parameters.AddWithValue((object?)insert.Source.BlockName ?? DBNull.Value);
                command.Parameters.AddWithValue((object?)insert.Source.LayerName ?? DBNull.Value);
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Json.JsonSerializer.Serialize(insert.Source.Attributes) });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = insert.Geometry });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = insert.OuterGeometry });
                await command.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            await using (var command = new NpgsqlCommand("""
                INSERT INTO spatial_features
                    (feature_type, block_name, source_file, layer_name, attributes, geom, outer_geom)
                VALUES
                    ($1, $2, $3, $4, $5, $6, $7);
                """, connection, transaction))
            {
                command.Parameters.AddWithValue(insert.Source.FeatureType);
                command.Parameters.AddWithValue((object?)insert.Source.BlockName ?? DBNull.Value);
                command.Parameters.AddWithValue((object?)insert.Source.SourceFile ?? DBNull.Value);
                command.Parameters.AddWithValue((object?)insert.Source.LayerName ?? DBNull.Value);
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Json.JsonSerializer.Serialize(insert.Source.Attributes) });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = insert.Geometry });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = insert.OuterGeometry });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        foreach (var delete in deletes)
        {
            var table = delete.IsBlockInstance ? "spatial_block_instances" : "spatial_features";
            await using var command = new NpgsqlCommand(
                $"DELETE FROM {table} WHERE id = $1 AND updated_at = $2;", connection, transaction);
            command.Parameters.AddWithValue(delete.Id);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = delete.ExpectedUpdatedAt });
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                conflictIds.Add(delete.Id);
            }
        }

        await SyncBlockDefinitionsAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return conflictIds;
    }

    private const int InstanceBatchSize = 500;

    /// <summary>
    /// Writes an entire DXF import (layer rows + block instances) in one transaction. Block
    /// instances are written via multi-row INSERT batches instead of one round trip per row,
    /// which matters once a layer has hundreds of instances (SmartLayout.dxf's Equipment layer
    /// alone has 126). Reports progress after each layer/batch and honors cancellation between
    /// statements; an exception or cancellation rolls the whole import back so no partial data
    /// is left behind.
    /// </summary>
    public async Task ImportAsync(
        IReadOnlyList<(string BlockName, string? LayerName, Geometry Geometry, Geometry OuterGeometry, IReadOnlyDictionary<string, string?> Attributes, string FeatureType)> layers,
        IReadOnlyList<(string BlockName, string? LayerName, Geometry Geometry, Geometry OuterGeometry, IReadOnlyDictionary<string, string?> Attributes)> instances,
        string sourceFile,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var total = layers.Count + instances.Count;
        var completed = 0;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var command = new NpgsqlCommand("""
                INSERT INTO spatial_features
                    (feature_type, block_name, source_file, layer_name, attributes, geom, outer_geom)
                VALUES
                    ($1, $2, $3, $4, $5, $6, $7);
                """, connection, transaction))
            {
                command.Parameters.AddWithValue(layer.FeatureType);
                command.Parameters.AddWithValue(layer.BlockName);
                command.Parameters.AddWithValue(sourceFile);
                command.Parameters.AddWithValue((object?)layer.LayerName ?? DBNull.Value);
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Json.JsonSerializer.Serialize(layer.Attributes) });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = layer.Geometry });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = layer.OuterGeometry });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            completed++;
            progress?.Report(new ImportProgress("Layers", completed, total));
        }

        for (var offset = 0; offset < instances.Count; offset += InstanceBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchCount = Math.Min(InstanceBatchSize, instances.Count - offset);
            await using (var command = new NpgsqlCommand(BuildInstanceBatchInsertSql(batchCount), connection, transaction))
            {
                for (var i = 0; i < batchCount; i++)
                {
                    var instance = instances[offset + i];
                    command.Parameters.AddWithValue(sourceFile);
                    command.Parameters.AddWithValue(instance.BlockName);
                    command.Parameters.AddWithValue((object?)instance.LayerName ?? DBNull.Value);
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Json.JsonSerializer.Serialize(instance.Attributes) });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = instance.Geometry });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = instance.OuterGeometry });
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            completed += batchCount;
            progress?.Report(new ImportProgress("Block instances", completed, total));
        }

        await SyncBlockDefinitionsAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string BuildInstanceBatchInsertSql(int rowCount)
    {
        var sql = new StringBuilder(
            "INSERT INTO spatial_block_instances (source_file, block_name, layer_name, attributes, geom, outer_geom) VALUES ");
        for (var i = 0; i < rowCount; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            var p = i * 6;
            sql.Append($"(${p + 1}, ${p + 2}, ${p + 3}, ${p + 4}, ${p + 5}, ${p + 6})");
        }

        sql.Append(';');
        return sql.ToString();
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}