// ============================================================================
// ChangeFeedRepository 전체 흐름도
//
//   [쓰기 쪽: 다른 Repository의 저장 트랜잭션 안에서 호출]
//   EnsureSchemaAsync(change_log 테이블/인덱스 보장)
//     → (데이터 변경 INSERT/UPDATE/DELETE)
//     → RecordAsync: change_log에 변경 항목 일괄 INSERT(unnest) → max(seq) 획득
//                    → pg_notify('drawing_changed', seq)   ※ 같은 트랜잭션이므로 커밋 시에만 전달됨
//
//   [읽기 쪽: ChangeListener가 알림을 받은 뒤 호출]
//   GetLatestSeqAsync(현재 기준선) / GetChangesSinceAsync(afterSeq 이후 변경을 레이어·사용자별 집계)
//     → 호출자가 ChangeBatch.LatestSeq를 새 기준선으로 저장
//   PurgeOldAsync: 보관 기간(기본 30일)이 지난 로그 삭제
//
// 사용 테이블: change_log(seq 식별자 PK, changed_at, user_id, user_name, session_id, layer_name,
//   entity_type, entity_id, op, layout_id). 인덱스: (layer_name, seq), (changed_at).
// 외부 의존: Npgsql, SpatialEditor.Domain의 ChangeContext/ChangeEntry/ChangeBatch/ChangeSummary,
//   PostgreSQL NOTIFY 채널 "drawing_changed"(수신은 ChangeListener).
// 트랜잭션·락 정책: RecordAsync는 호출자의 연결/트랜잭션을 그대로 사용(데이터 변경과 원자적으로
//   커밋/롤백되어 롤백된 저장은 알림되지 않음). 읽기/Purge는 데이터 소스에서 별도 연결을 열어
//   자동 커밋으로 실행하며 명시적 락은 사용하지 않는다. 알림 자체에는 데이터가 없어 유실되어도
//   다음 조회(seq 기준)에서 따라잡는다.
// ============================================================================
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
/// <remarks>
/// (한국어 요약) 다른 세션에 "저장된 데이터가 바뀌었다"를 알리는 변경 로그. 저장할 때마다 같은 트랜잭션에서
/// <c>change_log</c>에 기록하고 <c>NOTIFY</c>를 보내므로 롤백된 저장은 알려지지 않는다.
/// 알림에는 데이터가 없고, 수신자는 자신이 마지막으로 본 시퀀스 번호 이후의 변경을 조회한다.
/// </remarks>
public sealed class ChangeFeedRepository : IAsyncDisposable
{
    /// <summary>PostgreSQL LISTEN/NOTIFY 채널 이름("drawing_changed"). 송신(RecordAsync)과 수신(ChangeListener)이 공유한다.</summary>
    public const string NotifyChannel = "drawing_changed";

    /// <summary>읽기/정리용 연결을 제공하는 Npgsql 데이터 소스(연결 풀 포함).</summary>
    private readonly NpgsqlDataSource dataSource;

    /// <summary>저장소를 만든다.</summary>
    /// <param name="connectionString">PostgreSQL 연결 문자열(null 불가).</param>
    public ChangeFeedRepository(string connectionString)
    {
        dataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>내부 데이터 소스(연결 풀)를 해제한다.</summary>
    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    /// <summary>
    /// change_log 테이블과 인덱스를 보장한다(IF NOT EXISTS라 반복 호출해도 안전).
    /// layout_id 컬럼은 도면(레이아웃) 구분용으로 나중에 추가되었으며 기본값 1이다.
    /// </summary>
    /// <param name="connection">이미 열린 연결.</param>
    /// <param name="transaction">참여할 트랜잭션. null이면 자동 커밋.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
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
            ALTER TABLE change_log ADD COLUMN IF NOT EXISTS layout_id BIGINT NOT NULL DEFAULT 1;
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
    /// <param name="connection">데이터 변경에 사용한 것과 같은 열린 연결.</param>
    /// <param name="transaction">데이터 변경과 같은 트랜잭션(롤백되면 기록·알림도 함께 취소).</param>
    /// <param name="context">변경자 정보(사용자 ID/이름, 세션 GUID). null이면 세 값이 모두 NULL로 기록된다.</param>
    /// <param name="entries">기록할 변경 항목(레이어명, 엔티티 종류, 엔티티 ID(null=레이어 전체), 작업 I/U/D).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <param name="layoutId">변경이 속한 도면(레이아웃) ID. 기본 1. 수신 측이 도면별로 걸러내는 데 쓴다.</param>
    /// <returns>이번에 기록된 가장 큰 seq. entries가 비어 있으면 null(기록·알림 없음).</returns>
    internal static async Task<long?> RecordAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ChangeContext? context,
        IReadOnlyCollection<ChangeEntry> entries, CancellationToken cancellationToken, long layoutId = 1)
    {
        // 1) 변경이 없으면 로그도 알림도 만들지 않는다
        if (entries.Count == 0)
        {
            return null;
        }

        // 2) 항목 배열을 unnest로 한 번에 INSERT하고 최대 seq를 돌려받는다(왕복 1회)
        //    $1~$3: 변경자 정보, $4~$7: 항목별 배열(레이어/종류/ID/작업), $8: layout_id
        const string sql = """
            WITH ins AS (
                INSERT INTO change_log (user_id, user_name, session_id, layer_name, entity_type, entity_id, op, layout_id)
                SELECT $1, $2, $3, l, t, i, o::char(1), $8
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
        command.Parameters.Add(new NpgsqlParameter { Value = layoutId, NpgsqlDbType = NpgsqlDbType.Bigint });
        var seq = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        // 3) 같은 트랜잭션에서 NOTIFY: 커밋될 때에만 수신자에게 전달된다. 페이로드는 최대 seq 문자열
        await using var notify = new NpgsqlCommand("SELECT pg_notify($1, $2);", connection, transaction);
        notify.Parameters.AddWithValue(NotifyChannel);
        notify.Parameters.AddWithValue(seq.ToString());
        await notify.ExecuteNonQueryAsync(cancellationToken);
        return seq;
    }

    /// <summary>외부에서 호출하는 스키마 보장: 데이터 소스에서 연결을 열어 change_log를 준비한다(트랜잭션 없음).</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);
    }

    /// <summary>
    /// 현재 change_log의 가장 큰 seq를 반환한다(로그가 비어 있으면 0). 앱 시작 시 "여기까지는 이미 본 것"으로
    /// 삼는 기준선을 잡을 때 사용한다.
    /// </summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>최대 seq 또는 0.</returns>
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
    /// <param name="afterSeq">마지막으로 확인한 seq(이 값보다 큰 변경만 조회).</param>
    /// <param name="ownSession">자신의 세션 GUID. 이 세션이 만든 변경은 요약에서 제외(자기 변경은 알릴 필요 없음).</param>
    /// <param name="layers">관심 레이어 이름 목록(대소문자 무시). null이면 모든 레이어.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <param name="layoutId">조회할 도면(레이아웃) ID. 기본 1. 다른 도면의 변경은 요약에서 제외된다.</param>
    /// <returns>새 기준선(LatestSeq)과 레이어·사용자별 요약. 변경이 없으면 afterSeq 그대로, 요약은 빈 목록.</returns>
    public async Task<ChangeBatch> GetChangesSinceAsync(
        long afterSeq, Guid ownSession, IReadOnlyCollection<string>? layers, CancellationToken cancellationToken = default, long layoutId = 1)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // 1) afterSeq 이후의 최대 seq를 먼저 고정한다. 이후 집계를 seq <= latest로 제한하므로
        //    집계 중 새로 들어온 변경은 다음 조회로 넘어가 누락/중복이 생기지 않는다
        long latest;
        await using (var latestCommand = new NpgsqlCommand("SELECT COALESCE(max(seq), $1) FROM change_log WHERE seq > $1;", connection))
        {
            latestCommand.Parameters.AddWithValue(afterSeq);
            latest = (long)(await latestCommand.ExecuteScalarAsync(cancellationToken))!;
        }

        // 2) 새 변경이 없으면 빈 결과(기준선 유지)
        if (latest == afterSeq)
        {
            return new ChangeBatch(afterSeq, Array.Empty<ChangeSummary>());
        }

        // 3) (afterSeq, latest] 구간을 레이어·사용자별로 집계: 추가/수정/삭제 건수, 레이어 전체 변경(entity_id NULL) 여부.
        //    $1=afterSeq, $2=latest, $3=자기 세션, $4=레이어 목록(NULL이면 전체), $5=layout_id
        const string sql = """
            SELECT layer_name, user_name,
                   count(*) FILTER (WHERE op = 'I' AND entity_id IS NOT NULL),
                   count(*) FILTER (WHERE op = 'U'),
                   count(*) FILTER (WHERE op = 'D' AND entity_id IS NOT NULL),
                   bool_or(entity_id IS NULL)
            FROM change_log
            WHERE seq > $1 AND seq <= $2
              AND (session_id IS NULL OR session_id <> $3)
              AND layout_id = $5
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
        command.Parameters.Add(new NpgsqlParameter { Value = layoutId, NpgsqlDbType = NpgsqlDbType.Bigint });
        // 4) 결과 행을 ChangeSummary로 변환(user_name은 NULL 가능, count는 bigint라 int로 변환)
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

        // 5) 요약이 비어 있어도(자기 변경/다른 레이어뿐) 기준선은 latest로 전진시킨다
        return new ChangeBatch(latest, summaries);
    }

    /// <summary>Deletes log rows older than the retention period. Returns how many were removed.</summary>
    /// <param name="keepDays">보관 일수(기본 30). 이보다 오래된(changed_at 기준) 행을 삭제한다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>삭제된 행 수.</returns>
    public async Task<int> PurgeOldAsync(int keepDays = 30, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM change_log WHERE changed_at < now() - make_interval(days => $1);");
        command.Parameters.AddWithValue(keepDays);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
