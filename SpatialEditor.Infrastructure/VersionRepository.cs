// ============================================================================
// VersionRepository 전체 흐름도
//
//   [스키마] EnsureSchemaAsync: layout_versions / layout_version_blocks / layout_version_features 생성
//            (※ layouts, object_uid 컬럼은 PostGisFeatureRepository.EnsureSchemaAsync가 먼저 만들어 둔 상태가 전제)
//
//   [버전 저장] CreateVersionAsync
//        → RepeatableRead 트랜잭션 시작
//        → CreateVersionCoreAsync:
//             pg_advisory_xact_lock(layoutId)로 같은 도면의 버전 번호 채번 직렬화
//             → WorkingHashAsync(작업본 내용 해시 계산)
//             → 최신 버전 해시/다음 version_no/부모(base_version_id) 조회
//             → skipIfUnchanged이고 해시가 같으면 null 반환(저장 안 함)
//             → layout_versions INSERT → 블록/피처 스냅샷 복사 → 개수 갱신 → layouts.base_version_id 이동
//        → 커밋
//
//   [복원] RestoreVersionAsync
//        → 트랜잭션 → (작업본이 최신 버전과 다르면) "복원 전 자동 저장" 버전 생성(CreateVersionCoreAsync 재사용)
//        → 영향 레이어 수집 → 작업본 삭제 → 스냅샷을 작업본으로 복사(CopyVersionToWorkingCopyAsync)
//        → base_version_id 갱신 → 블록 정의 동기화/라이브러리 스키마 보장 → ChangeFeedRepository.RecordAsync(NOTIFY) → 커밋
//
//   [분기(브랜치)] CreateLayoutFromVersionAsync: layouts INSERT → 스냅샷을 새 layout_id의 작업본으로 복사 → 커밋
//   [비교] DiffAsync: (버전 또는 작업본) 두 상태를 병렬 조회 → VersionDiffer.Compare
//   [관리] ListLayouts/CreateLayout/RenameLayout/DeleteLayout, ListVersions/GetVersion/GetStatus,
//          UpdateVersion/SetStatus(기준선은 도면당 1개)/DeleteVersion(자식의 부모를 재연결)
//
// 사용 테이블: layouts, layout_versions, layout_version_blocks, layout_version_features,
//   spatial_block_instances, spatial_features, block_definitions, change_log(레이아웃 삭제 시).
// 외부 의존: Npgsql + NetTopologySuite(지오메트리 읽기), PostGisFeatureRepository.SyncBlockDefinitionsAsync,
//   LibraryRepository.EnsureSchemaAsync, ChangeFeedRepository.RecordAsync, Domain의 VersionDiffer 등.
// 트랜잭션·락 정책:
//   - 데이터 변경(저장/복원/분기/삭제/상태변경)은 모두 단일 트랜잭션으로 원자적으로 처리.
//   - 버전 저장은 RepeatableRead: 스냅샷 복사 도중 다른 사용자가 저장해도 "반반 섞인" 버전이 생기지 않음.
//   - 같은 도면의 version_no 채번은 pg_advisory_xact_lock(layout_id)로 직렬화(트랜잭션 종료 시 자동 해제).
//   - 내용 해시(content_hash)는 작업본과 스냅샷에 같은 SQL 식을 적용해 동일 내용이면 동일 해시가 되도록 함.
//   - 좌표계는 SRID 5186(미터). 읽기 전용 조회는 데이터 소스의 별도 연결을 사용(자동 커밋).
// ============================================================================
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
/// <remarks>
/// (한국어 요약) 도면(layout)의 버전 관리. layout은 작업본(layout_id가 붙은 spatial_block_instances /
/// spatial_features 행)과 여러 개의 불변 스냅샷 버전(layout_version_blocks / layout_version_features)을 가진다.
/// 객체는 버전이 바뀌어도 영구 식별자 object_uid를 유지하므로 두 버전 비교가 의미를 가진다.
/// 작업본을 바꾸는 모든 작업은 하나의 트랜잭션으로 수행되며 일반 저장처럼 변경 피드로 알려진다.
/// </remarks>
public sealed class VersionRepository : IAsyncDisposable
{
    /// <summary>쿼리·지오메트리 읽기용 데이터 소스(NetTopologySuite 매핑 포함, 연결 풀).</summary>
    private readonly NpgsqlDataSource dataSource;

    /// <summary>저장소를 만든다.</summary>
    /// <param name="connectionString">PostgreSQL 연결 문자열(null 불가).</param>
    public VersionRepository(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        // 지오메트리 컬럼을 NetTopologySuite Geometry로 읽고 쓰기 위한 플러그인 활성화
        builder.UseNetTopologySuite();
        dataSource = builder.Build();
    }

    /// <summary>내부 데이터 소스(연결 풀)를 해제한다.</summary>
    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    /// <summary>레이어명이 없는 객체를 가리키는 표시용 이름. (현재 코드에서는 SQL 내 리터럴 '(no layer)'와 같은 값을 쓴다.)</summary>
    private const string NoLayer = "(no layer)";

    // ------------------------------------------------------------------ schema

    /// <summary>Creates the version tables. Needs <see cref="PostGisFeatureRepository.EnsureSchemaAsync"/> to have run (layouts, object_uid).</summary>
    /// <remarks>
    /// 버전 테이블 3종(layout_versions, layout_version_blocks, layout_version_features)을 IF NOT EXISTS로 생성한다.
    /// 지오메트리는 SRID 5186(미터). layout_versions는 (layout_id, version_no) 유일 제약을 가지며,
    /// 레이아웃 삭제 시 ON DELETE CASCADE로 함께 지워진다. 트랜잭션 없이 한 번에 실행한다.
    /// </remarks>
    /// <param name="cancellationToken">취소 토큰.</param>
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
    // (같은 식을 작업본과 스냅샷에 모두 적용하므로 내용이 같으면 해시도 같다. 좌표는 4자리 반올림,
    //  object_uid 순 정렬, 지오메트리는 WKB의 md5로 반영한다. 별칭 t가 대상 테이블이다.)
    /// <summary>블록 내용 해시 SQL 조각: 행별 필드를 '|'로 이어 object_uid 순으로 string_agg 한 문자열(행이 없으면 빈 문자열).</summary>
    private const string BlockHashExpression = """
        COALESCE(string_agg(concat_ws('|', t.object_uid, t.block_name, t.layer_name,
            round(COALESCE(t.insert_x, 0)::numeric, 4), round(COALESCE(t.insert_y, 0)::numeric, 4),
            round(COALESCE(t.rotation_deg, 0)::numeric, 4), COALESCE(t.scale_x, 1), COALESCE(t.scale_y, 1),
            t.attributes::text, md5(ST_AsBinary(t.geom))), E'\n' ORDER BY t.object_uid), '')
        """;

    /// <summary>피처 내용 해시 SQL 조각: 종류/블록명/레이어/속성/지오메트리·외곽 지오메트리 md5를 object_uid 순으로 이어 붙인 문자열.</summary>
    private const string FeatureHashExpression = """
        COALESCE(string_agg(concat_ws('|', t.object_uid, t.feature_type, t.block_name, t.layer_name,
            t.attributes::text, md5(ST_AsBinary(t.geom)), md5(COALESCE(ST_AsBinary(t.outer_geom), ''::bytea))), E'\n' ORDER BY t.object_uid), '')
        """;

    /// <summary>작업본 해시 SQL. $1=layout_id. 블록 해시 + '#' + 피처(cad_block/dxf_layer만) 해시를 다시 md5 한다.</summary>
    private static string WorkingHashSql => $"""
        SELECT md5((SELECT {BlockHashExpression} FROM spatial_block_instances t WHERE t.layout_id = $1)
                || '#' || (SELECT {FeatureHashExpression} FROM spatial_features t WHERE t.layout_id = $1
                           AND t.feature_type IN ('cad_block', 'dxf_layer')));
        """;

    /// <summary>스냅샷 해시 SQL. $1=version_id. <see cref="WorkingHashSql"/>과 같은 식을 스냅샷 테이블에 적용한다.</summary>
    private static string VersionHashSql => $"""
        SELECT md5((SELECT {BlockHashExpression} FROM layout_version_blocks t WHERE t.version_id = $1)
                || '#' || (SELECT {FeatureHashExpression} FROM layout_version_features t WHERE t.version_id = $1));
        """;

    /// <summary>작업본의 내용 해시(md5 hex 32자)를 계산한다.</summary>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">참여할 트랜잭션(null이면 자동 커밋). 버전 저장 시에는 RepeatableRead 트랜잭션을 넘겨 스냅샷과 일관된 상태를 읽는다.</param>
    /// <param name="layoutId">대상 도면 ID.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>작업본 내용 해시. 버전의 content_hash와 비교해 변경 여부를 판단한다.</returns>
    private static async Task<string> WorkingHashAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long layoutId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(WorkingHashSql, connection, transaction);
        command.Parameters.AddWithValue(layoutId);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // ------------------------------------------------------------------ layouts

    /// <summary>모든 도면(layout) 목록을 id 순으로 반환한다. 각 도면의 객체 수(블록 + cad_block/dxf_layer 피처)를 함께 계산한다.</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>도면 정보 목록(없으면 빈 목록).</returns>
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

    /// <summary>빈 새 도면을 만든다(작업본 없음, 기준 버전 없음).</summary>
    /// <param name="name">도면 이름(null 불가).</param>
    /// <param name="description">설명. null 가능.</param>
    /// <param name="context">작성자 정보. UserName이 created_by_name에 기록되며 null이면 NULL.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>새 도면의 id.</returns>
    public async Task<long> CreateLayoutAsync(string name, string? description, ChangeContext? context, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO layouts (name, description, created_by_name) VALUES ($1, $2, $3) RETURNING id;");
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue((object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)context?.UserName ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>도면의 이름과 설명을 바꾼다.</summary>
    /// <param name="layoutId">대상 도면 ID.</param>
    /// <param name="name">새 이름(null 불가).</param>
    /// <param name="description">새 설명. null이면 설명을 지운다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
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
    /// <param name="layoutId">삭제할 도면 ID.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>삭제했으면 true. 마지막 남은 도면이라 거부했으면 false(아무 것도 변경하지 않음).</returns>
    public async Task<bool> DeleteLayoutAsync(long layoutId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 1) 도면이 1개뿐이면 삭제를 거부(앱은 항상 하나 이상의 도면이 필요). false 반환 시 트랜잭션은 커밋 없이 해제(롤백)된다
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM layouts;", connection, transaction))
        {
            if ((long)(await count.ExecuteScalarAsync(cancellationToken))! <= 1)
            {
                return false;
            }
        }

        // 2) 작업본 → 변경 로그 → 도면 순서로 삭제(layouts 삭제 시 layout_versions는 CASCADE로 함께 삭제됨)
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

        // 3) 어떤 인스턴스도 참조하지 않는 블록 정의 정리(삭제된 도면에만 쓰이던 정의 제거)
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
    /// <param name="versionId">복사 원본이 되는 버전 ID(새 도면의 base_version_id로 기록됨).</param>
    /// <param name="name">새 도면 이름.</param>
    /// <param name="description">설명. null 가능.</param>
    /// <param name="context">작성자 정보(null 가능).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>새로 만든 도면 ID.</returns>
    public async Task<long> CreateLayoutFromVersionAsync(
        long versionId, string name, string? description, ChangeContext? context, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1) 새 도면 행 생성(원본 버전을 기준 버전으로 기록)
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

        // 2) 스냅샷을 새 도면의 작업본으로 복사(object_uid 유지) → 블록 정의 동기화/라이브러리 스키마 보장 → 커밋
        await CopyVersionToWorkingCopyAsync(connection, transaction, versionId, layoutId, cancellationToken);
        await PostGisFeatureRepository.SyncBlockDefinitionsAsync(connection, transaction, cancellationToken);
        await LibraryRepository.EnsureSchemaAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return layoutId;
    }

    // ------------------------------------------------------------------ versions

    /// <summary>layout_versions(별칭 v)에서 읽는 컬럼 목록. <see cref="ReadVersion"/>의 서수 순서와 반드시 일치해야 한다.</summary>
    private const string VersionColumns =
        "v.id, v.layout_id, v.version_no, v.label, v.comment, v.parent_version_id, v.status, v.kind, v.created_by, v.created_by_name, v.created_at, v.block_count, v.feature_count, v.content_hash";

    /// <summary>현재 리더 행(<see cref="VersionColumns"/> 순서)을 <see cref="LayoutVersion"/>으로 변환한다. NULL 가능 컬럼은 null로 매핑.</summary>
    /// <param name="reader">행에 위치한 데이터 리더.</param>
    /// <returns>버전 정보.</returns>
    private static LayoutVersion ReadVersion(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5),
        reader.GetString(6), reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetDateTime(10), reader.GetInt32(11), reader.GetInt32(12), reader.GetString(13));

    /// <summary>도면의 버전 목록을 최신(version_no 큰 것) 순으로 반환한다.</summary>
    /// <param name="layoutId">대상 도면 ID.</param>
    /// <param name="includeAuto">true(기본)면 자동 저장 버전도 포함, false면 kind='manual'만.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>버전 목록(없으면 빈 목록).</returns>
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

    /// <summary>버전 ID로 버전 한 건을 조회한다.</summary>
    /// <param name="versionId">버전 ID.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>버전 정보. 없으면 null.</returns>
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
    /// <param name="layoutId">버전을 만들 도면 ID.</param>
    /// <param name="label">버전 이름(라벨, null 불가).</param>
    /// <param name="comment">설명. null 가능.</param>
    /// <param name="kind">버전 종류 문자열(예: 'manual', 자동 저장, 복원 전 저장 등. <c>VersionKind</c> 상수 참고).</param>
    /// <param name="context">작성자 정보(created_by / created_by_name에 기록, null 가능).</param>
    /// <param name="skipIfUnchanged">true(기본)면 작업본 해시가 최신 버전과 같을 때 새 버전을 만들지 않고 null 반환.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>생성된 버전. 변경이 없어 건너뛰었으면 null.</returns>
    /// <exception cref="InvalidOperationException">도면이 존재하지 않을 때.</exception>
    public async Task<LayoutVersion?> CreateVersionAsync(
        long layoutId, string label, string? comment, string kind, ChangeContext? context,
        bool skipIfUnchanged = true, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // RepeatableRead: 트랜잭션 시작 시점의 일관된 스냅샷으로 해시 계산과 복사를 모두 수행
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var created = await CreateVersionCoreAsync(connection, transaction, layoutId, label, comment, kind, context, skipIfUnchanged, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    /// <summary>
    /// 버전 생성의 핵심 로직. 호출자가 연 트랜잭션 안에서 실행되며 커밋은 호출자가 한다
    /// (복원 시 "복원 전 자동 저장"에도 같은 트랜잭션으로 재사용).
    /// </summary>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">호출자의 트랜잭션.</param>
    /// <param name="layoutId">대상 도면 ID.</param>
    /// <param name="label">버전 라벨.</param>
    /// <param name="comment">설명(null 가능).</param>
    /// <param name="kind">버전 종류.</param>
    /// <param name="context">작성자 정보(null 가능).</param>
    /// <param name="skipIfUnchanged">true면 최신 버전과 해시가 같을 때 null 반환.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>생성된 버전 또는(변경 없음으로 건너뛴 경우) null.</returns>
    /// <exception cref="InvalidOperationException">도면이 없을 때.</exception>
    private static async Task<LayoutVersion?> CreateVersionCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long layoutId, string label, string? comment, string kind,
        ChangeContext? context, bool skipIfUnchanged, CancellationToken cancellationToken)
    {
        // 1) 도면별 advisory lock(트랜잭션 종료 시 자동 해제): 동시에 저장해도 version_no가 겹치지 않게 직렬화
        // Serializes version numbering of one layout across sessions.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1);", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue(layoutId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2) 락을 얻은 뒤 작업본 내용 해시 계산 + 부모(현재 기준 버전)/다음 번호/최신 버전 해시 조회
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

        // 3) 내용이 최신 버전과 같으면 중복 버전을 만들지 않음
        if (skipIfUnchanged && string.Equals(newestHash, hash, StringComparison.Ordinal))
        {
            return null;
        }

        // 4) 버전 헤더 INSERT(부모 = 직전 기준 버전, 해시 기록)
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

        // 5) 작업본 블록/피처를 스냅샷 테이블로 복사(object_uid 보존, 피처는 cad_block/dxf_layer만)
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

        // 6) 복사된 행 수를 헤더에 반영
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

        // 7) 작업본이 "방금 만든 버전"에서 출발한 것으로 기준 버전을 이동(이후 변경 여부 판단의 기준)
        await using (var move = new NpgsqlCommand("UPDATE layouts SET base_version_id = $2 WHERE id = $1;", connection, transaction))
        {
            move.Parameters.AddWithValue(layoutId);
            move.Parameters.AddWithValue(versionId);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }

        // 8) 완성된 버전 행을 읽어 반환
        await using var read = new NpgsqlCommand($"SELECT {VersionColumns} FROM layout_versions v WHERE v.id = $1;", connection, transaction);
        read.Parameters.AddWithValue(versionId);
        await using var created = await read.ExecuteReaderAsync(cancellationToken);
        await created.ReadAsync(cancellationToken);
        return ReadVersion(created);
    }

    /// <summary>Where the working copy stands: which version it started from and whether it was changed since.</summary>
    /// <param name="layoutId">대상 도면 ID.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>도면 상태(기준 버전 번호/라벨, 수정 여부). 도면이 없으면 null.</returns>
    public async Task<LayoutStatus?> GetStatusAsync(long layoutId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // 1) 도면 이름과 기준 버전 ID 조회
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

        // 2) 기준 버전이 없으면(저장된 적 없음) 변경됨(true)으로 간주
        if (baseId is null)
        {
            return new LayoutStatus(layoutId, name, null, null, true);
        }

        // 3) 기준 버전의 번호/라벨/해시 조회
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

        // 4) 현재 작업본 해시와 기준 버전 해시를 비교해 수정 여부 결정(기준 버전이 삭제되어 해시가 없으면 수정됨으로 간주)
        var working = await WorkingHashAsync(connection, null, layoutId, cancellationToken);
        return new LayoutStatus(layoutId, name, baseNo, baseLabel, baseHash is null || !string.Equals(working, baseHash, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ items / diff

    /// <summary>JSONB 속성 컬럼(텍스트로 읽힘)을 문자열 사전으로 역직렬화한다. NULL/빈 JSON은 빈 사전.</summary>
    /// <param name="reader">행에 위치한 리더.</param>
    /// <param name="ordinal">속성 컬럼의 서수.</param>
    /// <returns>키-값 사전(null이 아님).</returns>
    private static IReadOnlyDictionary<string, string?> ReadAttributes(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? new Dictionary<string, string?>()
            : JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(ordinal)) ?? new Dictionary<string, string?>();

    /// <summary>블록 목록 조회 공통 로직. SQL은 지정된 12개 컬럼 순서(object_uid ~ outer_geom)를 반환해야 한다.</summary>
    /// <param name="sql">$1 하나를 받는 SELECT 문(<see cref="VersionBlocksSql"/> 또는 <see cref="WorkingBlocksSql"/>).</param>
    /// <param name="id">$1에 바인딩할 값(버전 ID 또는 도면 ID).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>블록 항목 목록(회전 NULL=0, 배율 NULL=1로 보정, 지오메트리는 SRID 5186 NTS 객체).</returns>
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

    /// <summary>피처 목록 조회 공통 로직. SQL은 8개 컬럼 순서(object_uid ~ outer_geom)를 반환해야 한다.</summary>
    /// <param name="sql">$1 하나를 받는 SELECT 문(<see cref="VersionFeaturesSql"/> 또는 <see cref="WorkingFeaturesSql"/>).</param>
    /// <param name="id">$1에 바인딩할 값(버전 ID 또는 도면 ID).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>피처 항목 목록(outer_geom은 NULL 가능).</returns>
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

    /// <summary>스냅샷 블록 조회 SQL($1=version_id).</summary>
    private const string VersionBlocksSql = """
        SELECT object_uid, block_name, layer_name, source_file, insert_x, insert_y, rotation_deg, scale_x, scale_y, attributes, geom, outer_geom
        FROM layout_version_blocks WHERE version_id = $1 ORDER BY block_name, object_uid;
        """;

    /// <summary>작업본 블록 조회 SQL($1=layout_id). 열 구성은 <see cref="VersionBlocksSql"/>과 동일.</summary>
    private const string WorkingBlocksSql = """
        SELECT object_uid, block_name, layer_name, source_file, insert_x, insert_y, rotation_deg, scale_x, scale_y, attributes, geom, outer_geom
        FROM spatial_block_instances WHERE layout_id = $1 ORDER BY block_name, object_uid;
        """;

    /// <summary>스냅샷 피처 조회 SQL($1=version_id).</summary>
    private const string VersionFeaturesSql = """
        SELECT object_uid, feature_type, block_name, layer_name, source_file, attributes, geom, outer_geom
        FROM layout_version_features WHERE version_id = $1 ORDER BY feature_type, object_uid;
        """;

    /// <summary>작업본 피처 조회 SQL($1=layout_id). 스냅샷에 담기는 종류(cad_block, dxf_layer)만 조회한다.</summary>
    private const string WorkingFeaturesSql = """
        SELECT object_uid, feature_type, block_name, layer_name, source_file, attributes, geom, outer_geom
        FROM spatial_features WHERE layout_id = $1 AND feature_type IN ('cad_block', 'dxf_layer') ORDER BY feature_type, object_uid;
        """;

    /// <summary>스냅샷 버전에 담긴 블록 목록을 조회한다.</summary>
    /// <param name="versionId">버전 ID.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    public Task<List<VersionBlockItem>> GetVersionBlocksAsync(long versionId, CancellationToken cancellationToken = default) =>
        ReadBlocksAsync(VersionBlocksSql, versionId, cancellationToken);

    /// <summary>스냅샷 버전에 담긴 피처 목록을 조회한다.</summary>
    /// <param name="versionId">버전 ID.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    public Task<List<VersionFeatureItem>> GetVersionFeaturesAsync(long versionId, CancellationToken cancellationToken = default) =>
        ReadFeaturesAsync(VersionFeaturesSql, versionId, cancellationToken);

    /// <summary>도면 작업본(현재 편집 중인 상태)의 블록 목록을 조회한다.</summary>
    /// <param name="layoutId">도면 ID.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    public Task<List<VersionBlockItem>> GetWorkingBlocksAsync(long layoutId, CancellationToken cancellationToken = default) =>
        ReadBlocksAsync(WorkingBlocksSql, layoutId, cancellationToken);

    /// <summary>
    /// Compares two states. <paramref name="fromVersionId"/> / <paramref name="toVersionId"/> null means "the
    /// working copy of <paramref name="layoutId"/>". The result describes what changed going from "from" to "to".
    /// </summary>
    /// <param name="layoutId">작업본을 비교 대상으로 쓸 때의 도면 ID.</param>
    /// <param name="fromVersionId">비교 시작 상태의 버전 ID. null이면 작업본.</param>
    /// <param name="toVersionId">비교 끝 상태의 버전 ID. null이면 작업본.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>from에서 to로 가면서 추가/삭제/변경된 항목(object_uid 기준 비교 결과).</returns>
    public async Task<VersionDiff> DiffAsync(long layoutId, long? fromVersionId, long? toVersionId, CancellationToken cancellationToken = default)
    {
        // 양쪽(from/to) × (블록/피처) 4개 조회를 동시에 시작하고(각각 별도 연결), 모두 끝나면 비교한다.
        // 주의: 별도 연결이라 작업본과 비교할 때 조회 사이에 다른 사용자의 저장이 끼어들 수 있다(읽기 전용 비교이므로 허용)
        var fromBlocks = fromVersionId is { } f ? ReadBlocksAsync(VersionBlocksSql, f, cancellationToken) : ReadBlocksAsync(WorkingBlocksSql, layoutId, cancellationToken);
        var toBlocks = toVersionId is { } t ? ReadBlocksAsync(VersionBlocksSql, t, cancellationToken) : ReadBlocksAsync(WorkingBlocksSql, layoutId, cancellationToken);
        var fromFeatures = fromVersionId is { } f2 ? ReadFeaturesAsync(VersionFeaturesSql, f2, cancellationToken) : ReadFeaturesAsync(WorkingFeaturesSql, layoutId, cancellationToken);
        var toFeatures = toVersionId is { } t2 ? ReadFeaturesAsync(VersionFeaturesSql, t2, cancellationToken) : ReadFeaturesAsync(WorkingFeaturesSql, layoutId, cancellationToken);
        await Task.WhenAll(fromBlocks, toBlocks, fromFeatures, toFeatures);
        return VersionDiffer.Compare(fromBlocks.Result, toBlocks.Result, fromFeatures.Result, toFeatures.Result);
    }

    // ------------------------------------------------------------------ restore / branch

    /// <summary>
    /// 스냅샷의 블록/피처를 지정 도면의 작업본 테이블로 복사한다(object_uid 유지). 기존 작업본은 지우지 않으므로
    /// 필요하면 호출 전에 삭제해야 한다. 호출자의 트랜잭션에 참여한다.
    /// </summary>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">호출자의 트랜잭션.</param>
    /// <param name="versionId">복사 원본 버전 ID.</param>
    /// <param name="layoutId">복사 대상 도면 ID(작업본 행의 layout_id로 기록).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
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
    /// <param name="layoutId">복원할 도면 ID(작업본이 교체됨).</param>
    /// <param name="versionId">복원 원본 버전 ID.</param>
    /// <param name="context">변경자 정보(안전 저장 버전의 작성자 및 변경 피드 기록용, null 가능).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>복원 전 자동 저장된 버전. 작업본이 최신 버전과 같아 저장이 필요 없었으면 null.</returns>
    /// <exception cref="InvalidOperationException">복원할 버전이 더 이상 존재하지 않을 때.</exception>
    public async Task<LayoutVersion?> RestoreVersionAsync(
        long layoutId, long versionId, ChangeContext? context, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1) 복원 원본 버전 확인(없으면 예외 → 트랜잭션 롤백)
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

        // 2) 복원 전 작업본을 안전 버전으로 저장(최신 버전과 같으면 생성 생략). 같은 트랜잭션이므로 복원 실패 시 함께 취소됨
        var safety = await CreateVersionCoreAsync(
            connection, transaction, layoutId, $"복원 전 자동 저장 (v{versionNo} 복원)", null, VersionKind.PreRestore, context,
            skipIfUnchanged: true, cancellationToken);

        // 3) 복원 전/후에 존재하는 모든 레이어를 모은다(다른 세션에는 이들 모두가 바뀐 것으로 보이므로 변경 피드에 기록)
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

        // 4) 현재 작업본 삭제(스냅샷이 다루는 종류의 피처만 삭제; 그 외 피처 종류는 유지)
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

        // 5) 스냅샷을 작업본으로 복사하고 기준 버전을 복원한 버전으로 이동
        await CopyVersionToWorkingCopyAsync(connection, transaction, versionId, layoutId, cancellationToken);
        await using (var baseUpdate = new NpgsqlCommand("UPDATE layouts SET base_version_id = $2 WHERE id = $1;", connection, transaction))
        {
            baseUpdate.Parameters.AddWithValue(layoutId);
            baseUpdate.Parameters.AddWithValue(versionId);
            await baseUpdate.ExecuteNonQueryAsync(cancellationToken);
        }

        // 6) 블록 정의 동기화 → 라이브러리 스키마 보장 → 변경 피드 기록(NOTIFY) → 커밋(전부 원자적)
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

    /// <summary>버전의 라벨과 설명을 수정한다. 기준선(baseline) 버전은 수정할 수 없다.</summary>
    /// <param name="versionId">대상 버전 ID.</param>
    /// <param name="label">새 라벨.</param>
    /// <param name="comment">새 설명. null이면 지운다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <exception cref="InvalidOperationException">기준선 버전이거나 버전이 없어 갱신된 행이 없을 때.</exception>
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
    /// <param name="versionId">대상 버전 ID.</param>
    /// <param name="status">새 상태(<c>VersionStatus</c>의 Draft/Released/Baseline/Archived 중 하나).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <exception cref="ArgumentException">알 수 없는 상태 값일 때.</exception>
    public async Task SetStatusAsync(long versionId, string status, CancellationToken cancellationToken = default)
    {
        if (status is not (VersionStatus.Draft or VersionStatus.Released or VersionStatus.Baseline or VersionStatus.Archived))
        {
            throw new ArgumentException("Unknown status.", nameof(status));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 기준선으로 지정하는 경우, 같은 도면의 기존 기준선을 먼저 released로 내려 도면당 1개만 유지(같은 트랜잭션)
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
    /// <param name="versionId">삭제할 버전 ID. 존재하지 않으면 아무 일도 하지 않고 반환한다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <exception cref="InvalidOperationException">기준선 버전을 삭제하려 할 때.</exception>
    public async Task DeleteVersionAsync(long versionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1) 대상 행을 FOR UPDATE로 잠그고 상태/부모를 읽는다(동시에 상태 변경·삭제되는 것을 방지)
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

        // 2) 자식 버전과 작업본 기준 버전을 삭제 대상의 부모로 재연결한 뒤 삭제(스냅샷 행은 CASCADE로 함께 삭제)
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
