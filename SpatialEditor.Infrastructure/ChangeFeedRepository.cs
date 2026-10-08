using Npgsql;
using NpgsqlTypes;
using SpatialEditor.Domain;

namespace SpatialEditor.Infrastructure;

/// <summary>
/// Change log used to tell other sessions that saved data changed. Every save writes its rows to
/// <c>change_log</c> and sends a <c>NOTIFY</c> in the same transaction, so a rolled-back save is
/// never announced. The notification carries no data: receivers read what changed after the last
/// sequence number they have seen, which also makes a lost notification harmless.
/// </summary>
public sealed class ChangeFeedRepository : IAsyncDisposable
{
    public const string NotifyChannel = "drawing_changed";

    private readonly NpgsqlDataSource dataSource;

    public ChangeFeedRepository(string connectionString)
    {
        dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    internal static async Task EnsureSchemaAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS change_log (
                seq BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                changed_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                user_id BIGINT,
                user_name TEXT,
                session_id UUID,
                layer_name VARCHAR(255) NOT NULL,
                entity_type VARCHAR(30) NOT NULL,
                entity_id BIGINT,
                op CHAR(1) NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_change_log_layer_seq ON change_log (layer_name, seq);
            CREATE INDEX IF NOT EXISTS idx_change_log_changed_at ON change_log (changed_at);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Records changes and announces them. Must run on the same connection/transaction as the data
    /// change so both commit or roll back together. Returns the highest sequence written (or null).
    /// </summary>
    internal static async Task<long?> RecordAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ChangeContext? context,
        IReadOnlyCollection<ChangeEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        const string sql = """
            WITH ins AS (
                INSERT INTO change_log (user_id, user_name, session_id, layer_name, entity_type, entity_id, op)
                SELECT $1, $2, $3, l, t, i, o::char(1)
                FROM unnest($4::text[], $5::text[], $6::bigint[], $7::text[]) AS e(l, t, i, o)
                RETURNING seq
            )
            SELECT max(seq) FROM ins;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)context?.UserId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)context?.UserName ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = context is null ? DBNull.Value : context.SessionId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = entries.Select(e => e.LayerName).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = entries.Select(e => e.EntityType).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = entries.Select(e => e.EntityId).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter { Value = entries.Select(e => e.Operation.ToString()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        var seq = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        await using var notify = new NpgsqlCommand("SELECT pg_notify($1, $2);", connection, transaction);
        notify.Parameters.AddWithValue(NotifyChannel);
        notify.Parameters.AddWithValue(seq.ToString());
        await notify.ExecuteNonQueryAsync(cancellationToken);
        return seq;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);
    }

    public async Task<long> GetLatestSeqAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT COALESCE(max(seq), 0) FROM change_log;");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Changes after <paramref name="afterSeq"/>, grouped by layer and user. Changes made by
    /// <paramref name="ownSession"/> and, when <paramref name="layers"/> is given, on other layers are
    /// left out of the summaries — but <see cref="ChangeBatch.LatestSeq"/> still covers them so the
    /// caller's baseline moves past everything it has looked at.
    /// </summary>
    public async Task<ChangeBatch> GetChangesSinceAsync(
        long afterSeq, Guid ownSession, IReadOnlyCollection<string>? layers, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        long latest;
        await using (var latestCommand = new NpgsqlCommand("SELECT COALESCE(max(seq), $1) FROM change_log WHERE seq > $1;", connection))
        {
            latestCommand.Parameters.AddWithValue(afterSeq);
            latest = (long)(await latestCommand.ExecuteScalarAsync(cancellationToken))!;
        }

        if (latest == afterSeq)
        {
            return new ChangeBatch(afterSeq, Array.Empty<ChangeSummary>());
        }

        const string sql = """
            SELECT layer_name, user_name,
                   count(*) FILTER (WHERE op = 'I' AND entity_id IS NOT NULL),
                   count(*) FILTER (WHERE op = 'U'),
                   count(*) FILTER (WHERE op = 'D' AND entity_id IS NOT NULL),
                   bool_or(entity_id IS NULL)
            FROM change_log
            WHERE seq > $1 AND seq <= $2
              AND (session_id IS NULL OR session_id <> $3)
              AND ($4::text[] IS NULL OR lower(layer_name) = ANY($4::text[]))
            GROUP BY layer_name, user_name
            ORDER BY layer_name, user_name;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(afterSeq);
        command.Parameters.AddWithValue(latest);
        command.Parameters.Add(new NpgsqlParameter { Value = ownSession, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = layers is null ? DBNull.Value : layers.Select(layer => layer.ToLowerInvariant()).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var summaries = new List<ChangeSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new ChangeSummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                (int)reader.GetInt64(2),
                (int)reader.GetInt64(3),
                (int)reader.GetInt64(4),
                reader.GetBoolean(5)));
        }

        return new ChangeBatch(latest, summaries);
    }

    /// <summary>Deletes log rows older than the retention period. Returns how many were removed.</summary>
    public async Task<int> PurgeOldAsync(int keepDays = 30, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM change_log WHERE changed_at < now() - make_interval(days => $1);");
        command.Parameters.AddWithValue(keepDays);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
