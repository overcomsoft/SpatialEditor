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
                outer_geom GEOMETRY(Polygon, 5186),
                created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            ALTER TABLE spatial_features ADD COLUMN IF NOT EXISTS block_name VARCHAR(255);
            ALTER TABLE spatial_features ADD COLUMN IF NOT EXISTS source_file VARCHAR(1024);
            ALTER TABLE spatial_features ADD COLUMN IF NOT EXISTS layer_name VARCHAR(255);
            ALTER TABLE spatial_features ADD COLUMN IF NOT EXISTS outer_geom GEOMETRY(Polygon, 5186);

            CREATE INDEX IF NOT EXISTS idx_spatial_features_geom ON spatial_features USING GIST (geom);
            CREATE INDEX IF NOT EXISTS idx_spatial_features_outer_geom ON spatial_features USING GIST (outer_geom);
            CREATE INDEX IF NOT EXISTS idx_spatial_features_attributes ON spatial_features USING GIN (attributes);
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
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
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, feature_type, outer_geom, attributes
            FROM spatial_features
            WHERE outer_geom IS NOT NULL
                            AND feature_type IN ('cad_block', 'dxf_layer')
            ORDER BY id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        var features = new List<SpatialFeature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var attributes = reader.IsDBNull(3)
                ? new Dictionary<string, string?>()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(3))
                    ?? new Dictionary<string, string?>();
            features.Add(new SpatialFeature(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetFieldValue<Geometry>(2),
                attributes));
        }

        return features;
    }

    public async Task InsertCadBlockAsync(
        string blockName,
        string? layerName,
        string sourceFile,
        Geometry actualGeometry,
        Polygon outerPolygon,
        IReadOnlyDictionary<string, string?> attributes,
        string featureType = "cad_block",
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO spatial_features
                (feature_type, block_name, source_file, layer_name, attributes, geom, outer_geom)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7);
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(featureType);
        command.Parameters.AddWithValue(blockName);
        command.Parameters.AddWithValue((object?)sourceFile ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)layerName ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Json.JsonSerializer.Serialize(attributes) });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = actualGeometry });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = outerPolygon });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}