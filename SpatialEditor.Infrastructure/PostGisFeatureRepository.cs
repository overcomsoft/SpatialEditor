// ============================================================================
// PostGisFeatureRepository 전체 흐름도
//
//   [스키마 보장] EnsureSchemaAsync
//        PostGIS 확장 → spatial_features / spatial_block_instances / layouts(기본 도면 id=1) / block_definitions
//        → layout_id·object_uid 컬럼과 유일 인덱스 → GIST/GIN 인덱스
//        → SyncBlockDefinitionsAsync(변환 컬럼 채움, 블록 정의 생성, 인스턴스-정의 연결)
//        → LibraryRepository.EnsureSchemaAsync → ChangeFeedRepository.EnsureSchemaAsync
//
//   [가져오기] ImportAsync (단일 트랜잭션)
//        레이어 행 INSERT(건별) → 블록 인스턴스 다중 행 INSERT(500건 배치)
//        → SyncBlockDefinitionsAsync → LibraryRepository.EnsureSchemaAsync
//        → ChangeFeedRepository.RecordAsync(INSERT 기록 + NOTIFY) → 커밋(예외/취소 시 전체 롤백)
//
//   [조회] FindByBoundsAsync / FindImported*Async / FindImportedLayersAsync / FindImportedCountsAsync /
//          FindEditableLayerObjectsAsync / FindEditableBlockInstancesAsync / LayerExistsAsync / HasImportedFileAsync
//          (모두 현재 layout_id 범위로 제한, 자동 커밋 읽기)
//
//   [편집 저장] ApplyLayerEditsAsync (단일 트랜잭션)
//        UPDATE(updated_at 일치할 때만; 불일치=충돌 ID 수집) → INSERT → DELETE(updated_at 일치할 때만)
//        → SyncBlockDefinitionsAsync → LibraryRepository.EnsureSchemaAsync
//        → ChangeFeedRepository.RecordAsync(NOTIFY) → 커밋 → 충돌 ID 목록 반환
//
//   [삭제/이름 변경] DeleteImportedFileAsync(파일 단위) / DeleteLayerAsync / RenameLayerAsync(레이어 단위)
//        → 필요 시 미사용 block_definitions 정리 → 변경 피드 기록
//
// 사용 테이블: spatial_features, spatial_block_instances, block_definitions, layouts
//   (+ SyncBlockDefinitionsAsync는 VersionRepository에서도 재사용, change_log는 ChangeFeedRepository 경유).
//   좌표계는 모두 SRID 5186(미터). 객체는 영구 식별자 object_uid를 가지며 (layout_id, object_uid)가 유일.
// 외부 의존: Npgsql + NetTopologySuite(지오메트리 매핑), LibraryRepository, ChangeFeedRepository, Domain 모델.
// 트랜잭션·락 정책:
//   - 쓰기 작업(Import/ApplyLayerEdits/DeleteLayer/RenameLayer)은 단일 트랜잭션(기본 READ COMMITTED).
//   - 동시 편집은 낙관적 동시성: updated_at이 읽을 때 값과 같을 때만 UPDATE/DELETE. 0행이면 충돌로 보고하고
//     다른 세션의 변경을 덮어쓰지 않는다(충돌이 아닌 나머지 변경은 그대로 커밋).
//   - 변경 기록(change_log)과 NOTIFY는 데이터 변경과 같은 트랜잭션에서 수행해 롤백 시 알려지지 않음.
//   - 모든 작업 쿼리는 이 저장소의 layout_id(도면)로 제한된다(@@L@@ 자리표시자 치환 또는 보간).
//   - 명시적 advisory lock은 사용하지 않는다(버전 번호 채번 락은 VersionRepository에 있음).
//   - DeleteImportedFileAsync는 데이터 삭제 3건을 트랜잭션 없이(자동 커밋) 실행한 뒤 변경 기록만 별도
//     트랜잭션으로 남긴다는 점에 유의(코드 그대로의 동작).
// ============================================================================
using System.Text;
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using SpatialEditor.Domain;

namespace SpatialEditor.Infrastructure;

/// <summary>
/// PostGIS(PostgreSQL)에 DXF 가져오기 결과와 편집 내용을 저장/조회하는 저장소.
/// 한 인스턴스는 하나의 도면(layout_id)에만 연결되어 해당 도면의 작업본만 읽고 쓴다.
/// 모든 지오메트리는 SRID 5186(미터 단위)이다.
/// </summary>
public sealed class PostGisFeatureRepository : IAsyncDisposable
{
    /// <summary>쿼리 실행용 데이터 소스(NetTopologySuite 매핑 포함, 연결 풀).</summary>
    private readonly NpgsqlDataSource dataSource;
    /// <summary>이 저장소가 다루는 도면(layout) ID. 모든 작업본 쿼리를 이 값으로 제한한다.</summary>
    private readonly long layoutId;

    /// <summary>
    /// <paramref name="layoutId"/> selects which drawing (layout) this repository reads and writes; every
    /// query on the working copy is limited to it. Layout 1 is the default drawing.
    /// </summary>
    /// <param name="connectionString">PostgreSQL 연결 문자열(null 불가).</param>
    /// <param name="layoutId">대상 도면 ID. 기본 1(기본 도면).</param>
    public PostGisFeatureRepository(string connectionString, long layoutId = 1)
    {
        this.layoutId = layoutId;
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        // 지오메트리 컬럼을 NetTopologySuite Geometry로 읽고 쓰기 위한 플러그인 활성화
        builder.UseNetTopologySuite();
        dataSource = builder.Build();
    }

    /// <summary>이 저장소가 연결된 도면 ID.</summary>
    public long LayoutId => layoutId;

    /// <summary>Fills the <c>@@L@@</c> placeholder with this repository's layout id.</summary>
    /// <param name="sql">자리표시자 @@L@@를 포함한 SQL 문자열.</param>
    /// <returns>@@L@@가 layout_id 숫자(문화권 무관)로 치환된 SQL. 숫자(long)만 삽입되므로 SQL 주입 위험은 없다.</returns>
    private string Q(string sql) => sql.Replace("@@L@@", layoutId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>DB 연결과 PostGIS 설치 여부를 확인한다.</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>"Connected: database=..., user=..., PostGIS=..." 형식의 상태 문자열.</returns>
    /// <exception cref="InvalidOperationException">서버가 정보를 반환하지 않았을 때.</exception>
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

    /// <summary>
    /// 필요한 테이블/컬럼/인덱스를 모두 보장한다(IF NOT EXISTS로 반복 실행 안전, 구버전 DB도 ALTER로 승급).
    /// 지오메트리는 SRID 5186(미터). layouts에 기본 도면(id=1)을 만들고, 기존 행에는 layout_id=1과
    /// 무작위 object_uid가 채워진다. 마지막에 블록 정의 동기화 및 라이브러리/변경 피드 스키마를 보장한다.
    /// 스키마 SQL은 트랜잭션 없이 한 번의 명령으로 실행된다.
    /// </summary>
    /// <param name="cancellationToken">취소 토큰.</param>
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

            CREATE TABLE IF NOT EXISTS layouts (
                id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                name VARCHAR(255) NOT NULL,
                description TEXT,
                base_version_id BIGINT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                created_by_name VARCHAR(255)
            );
            INSERT INTO layouts (id, name) SELECT 1, '기본 도면' WHERE NOT EXISTS (SELECT 1 FROM layouts WHERE id = 1);
            SELECT setval(pg_get_serial_sequence('layouts', 'id'), GREATEST((SELECT max(id) FROM layouts), 1));

            ALTER TABLE spatial_features ADD COLUMN IF NOT EXISTS layout_id BIGINT NOT NULL DEFAULT 1;
            ALTER TABLE spatial_features ADD COLUMN IF NOT EXISTS object_uid UUID NOT NULL DEFAULT gen_random_uuid();
            ALTER TABLE spatial_block_instances ADD COLUMN IF NOT EXISTS layout_id BIGINT NOT NULL DEFAULT 1;
            ALTER TABLE spatial_block_instances ADD COLUMN IF NOT EXISTS object_uid UUID NOT NULL DEFAULT gen_random_uuid();
            CREATE INDEX IF NOT EXISTS idx_spatial_features_layout ON spatial_features (layout_id);
            CREATE INDEX IF NOT EXISTS idx_spatial_block_instances_layout ON spatial_block_instances (layout_id);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_spatial_features_uid ON spatial_features (layout_id, object_uid);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_spatial_block_instances_uid ON spatial_block_instances (layout_id, object_uid);

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
        // (정의/변환 컬럼이 생기기 전에 가져온 인스턴스를 채워 넣는다. 이어서 라이브러리·변경 피드 스키마도 보장)
        await SyncBlockDefinitionsAsync(connection, null, cancellationToken);
        await LibraryRepository.EnsureSchemaAsync(connection, null, cancellationToken);
        await ChangeFeedRepository.EnsureSchemaAsync(connection, null, cancellationToken);
    }

    /// <summary>
    /// Parses a numeric attribute out of the instance's JSONB, yielding NULL for missing or
    /// malformed values so one bad row can never abort the whole sync.
    /// </summary>
    /// <param name="key">JSONB attributes에서 읽을 키 이름(코드 상수만 전달; SQL에 직접 삽입되므로 외부 입력 금지).</param>
    /// <returns>숫자 형식이면 double precision으로 변환하고, 아니면 NULL이 되는 SQL CASE 식.</returns>
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
    /// <remarks>
    /// 특정 도면에 한정되지 않고 모든 도면(layout)의 인스턴스를 대상으로 하며, 이미 일치하는 행은 건드리지 않는다.
    /// 로컬 좌표 변환은 월드 지오메트리에서 삽입점 평행이동의 역변환과 회전·배율의 역변환(ST_Affine)을 적용한다.
    /// 회전은 도(degree) 단위 컬럼이며 배율 0인 행은 정의 생성에서 제외된다.
    /// </remarks>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">참여할 트랜잭션. null이면 자동 커밋.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    internal static async Task SyncBlockDefinitionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        // 1) attributes의 배치 속성(삽입 좌표/회전/배율)을 실제 컬럼으로 복사(값이 달라진 행만 UPDATE)
        // 2) (source_file, block_name)별로 정의 1건 생성: 수정된 적 없는(updated_at = created_at) 인스턴스를 우선 사용
        // 3) 정의가 없는 인스턴스를 같은 (source_file, block_name)의 정의에 연결
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

    /// <summary>지정한 사각형 범위와 겹치는(bbox 교차, <c>&amp;&amp;</c>) 피처를 id 순으로 조회한다.</summary>
    /// <param name="bounds">조회 범위(좌표 단위는 srid 좌표계의 단위; 5186이면 미터).</param>
    /// <param name="srid">범위 좌표의 SRID. 기본 5186. 저장된 지오메트리의 SRID와 같아야 한다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>피처 목록(속성이 NULL이면 빈 사전).</returns>
    public async Task<IReadOnlyList<SpatialFeature>> FindByBoundsAsync(
        Envelope bounds,
        int srid = 5186,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, feature_type, geom, attributes
            FROM spatial_features
            WHERE layout_id = @@L@@ AND geom && ST_MakeEnvelope($1, $2, $3, $4, $5)
            ORDER BY id;
            """;

        await using var command = dataSource.CreateCommand(Q(sql));
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

    /// <summary>
    /// 가져온 피처(cad_block, dxf_layer)의 외곽 지오메트리(outer_geom)를 ST_Dump로 단일 도형으로 분해해 조회한다.
    /// </summary>
    /// <param name="layerNames">대상 레이어명 목록. null 또는 비어 있으면 전체 레이어. 레이어가 없는 행은 "(no layer)"로 비교한다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>id 순 피처 목록(LayerName 포함, 속성 NULL이면 빈 사전).</returns>
    public async Task<IReadOnlyList<SpatialFeature>> FindImportedOuterPolygonsAsync(
        IReadOnlyCollection<string>? layerNames = null,
        CancellationToken cancellationToken = default)
    {
        var sql = """
                        SELECT f.id, f.feature_type, f.layer_name, dumped.geom, f.attributes
                        FROM spatial_features f
                        CROSS JOIN LATERAL ST_Dump(f.outer_geom) AS dumped
                        WHERE f.outer_geom IS NOT NULL AND f.layout_id = @@L@@
                            AND f.feature_type IN ('cad_block', 'dxf_layer')
            """;
        if (layerNames is { Count: > 0 })
        {
            sql += " AND COALESCE(f.layer_name, '(no layer)') = ANY($1)";
        }

        sql += " ORDER BY id;";

        await using var command = dataSource.CreateCommand(Q(sql));
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

    /// <summary>블록 인스턴스의 외곽 지오메트리(outer_geom)를 ST_Dump로 분해하여 'block_instance' 종류 피처로 조회한다(레이어 필터 없음).</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>(id, 경로) 순 피처 목록.</returns>
    public async Task<IReadOnlyList<SpatialFeature>> FindImportedBlockInstanceOutersAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT i.id, 'block_instance', i.layer_name, dumped.geom, i.attributes
            FROM spatial_block_instances i
            CROSS JOIN LATERAL ST_Dump(i.outer_geom) AS dumped
            WHERE i.outer_geom IS NOT NULL AND i.layout_id = @@L@@
            ORDER BY i.id, dumped.path;
            """;

        await using var command = dataSource.CreateCommand(Q(sql));
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

    /// <summary>블록 인스턴스의 실제 도형(geom)을 ST_Dump로 분해하여 'block_instance' 종류 피처로 조회한다.</summary>
    /// <param name="layerNames">대상 레이어명 목록. null 또는 비어 있으면 전체. 레이어 없는 행은 "(no layer)"로 비교.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>(id, 경로) 순 피처 목록.</returns>
    public async Task<IReadOnlyList<SpatialFeature>> FindImportedBlockInstanceGeometriesAsync(
        IReadOnlyCollection<string>? layerNames = null,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT i.id, 'block_instance', i.layer_name, dumped.geom, i.attributes
            FROM spatial_block_instances i
            CROSS JOIN LATERAL ST_Dump(i.geom) AS dumped
            WHERE i.geom IS NOT NULL AND i.layout_id = @@L@@
            """;
        if (layerNames is { Count: > 0 })
        {
            sql += " AND COALESCE(i.layer_name, '(no layer)') = ANY($1)";
        }

        sql += " ORDER BY i.id, dumped.path;";

        await using var command = dataSource.CreateCommand(Q(sql));
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

    /// <summary>
    /// 가져온 레이어 목록과 레이어별 도형 종류(Point/Line/Polygon, 블록만 있으면 "Block"), 도형 수, 블록 인스턴스 수를 집계한다.
    /// </summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>레이어명 순 목록. 레이어가 없는 객체는 "(no layer)" 이름으로 묶인다.</returns>
    public async Task<IReadOnlyList<LayerInfo>> FindImportedLayersAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH layer_names AS (
                SELECT DISTINCT COALESCE(layer_name, '(no layer)') AS layer_name
                FROM spatial_features
                WHERE feature_type IN ('cad_block', 'dxf_layer') AND layout_id = @@L@@
                UNION
                SELECT DISTINCT COALESCE(layer_name, '(no layer)') AS layer_name
                FROM spatial_block_instances
                WHERE layout_id = @@L@@
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
                WHERE f.feature_type IN ('cad_block', 'dxf_layer') AND f.layout_id = @@L@@
                GROUP BY COALESCE(f.layer_name, '(no layer)')
            ),
            instance_counts AS (
                SELECT COALESCE(layer_name, '(no layer)') AS layer_name,
                       COUNT(*) AS instance_count
                FROM spatial_block_instances
                WHERE layout_id = @@L@@
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

        await using var command = dataSource.CreateCommand(Q(sql));
        var layers = new List<LayerInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            layers.Add(new LayerInfo(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
        }

        return layers;
    }

    /// <summary>
    /// 특정 원본 파일(source_file)에서 가져온 모든 데이터(피처, 블록 인스턴스, 더 이상 쓰이지 않는 블록 정의)를 삭제하고
    /// 영향을 받은 레이어를 변경 피드에 기록한다.
    /// </summary>
    /// <remarks>
    /// 주의: 데이터 삭제 3건은 명시적 트랜잭션 없이 각각 자동 커밋으로 실행되고, 변경 기록만 마지막에 별도 트랜잭션으로 남긴다.
    /// </remarks>
    /// <param name="sourceFile">삭제할 원본 파일 이름/경로(가져올 때 저장한 source_file 값과 동일해야 함).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <param name="changeContext">변경자 정보(변경 피드용, null 가능).</param>
    public async Task DeleteImportedFileAsync(
        string sourceFile, CancellationToken cancellationToken = default, ChangeContext? changeContext = null)
    {
        // 1) 행이 사라지기 전에 영향을 받을 레이어 이름을 먼저 읽어 둔다(변경 피드 기록용)
        // The layers have to be read before their rows are gone.
        var affectedLayers = new List<string>();
        await using (var layerConnection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var layerCommand = new NpgsqlCommand("""
            SELECT DISTINCT COALESCE(layer_name, '(no layer)') FROM spatial_features WHERE source_file = $1 AND layout_id = @@L@@
            UNION
            SELECT DISTINCT COALESCE(layer_name, '(no layer)') FROM spatial_block_instances WHERE source_file = $1 AND layout_id = @@L@@;
            """.Replace("@@L@@", layoutId.ToString()), layerConnection))
        {
            layerCommand.Parameters.AddWithValue(sourceFile);
            await using var layerReader = await layerCommand.ExecuteReaderAsync(cancellationToken);
            while (await layerReader.ReadAsync(cancellationToken))
            {
                affectedLayers.Add(layerReader.GetString(0));
            }
        }

        // 2) 이 도면의 해당 파일 피처를 삭제한 뒤 블록 인스턴스 삭제
        const string sql = """
            DELETE FROM spatial_features
            WHERE source_file = $1 AND layout_id = @@L@@
              AND feature_type IN ('cad_block', 'dxf_layer');
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(Q(sql), connection);
        command.Parameters.AddWithValue(sourceFile);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var instanceCommand = new NpgsqlCommand(
            Q("DELETE FROM spatial_block_instances WHERE source_file = $1 AND layout_id = @@L@@;"), connection);
        instanceCommand.Parameters.AddWithValue(sourceFile);
        await instanceCommand.ExecuteNonQueryAsync(cancellationToken);

        // 3) 인스턴스가 정의를 참조하므로 정의는 마지막에 삭제(다른 인스턴스가 아직 쓰는 정의는 남김)
        // Instances reference their definitions, so definitions go last.
        await using var definitionCommand = new NpgsqlCommand(
            "DELETE FROM block_definitions d WHERE d.source_file = $1 AND NOT EXISTS (SELECT 1 FROM spatial_block_instances i WHERE i.definition_id = d.id);", connection);
        definitionCommand.Parameters.AddWithValue(sourceFile);
        await definitionCommand.ExecuteNonQueryAsync(cancellationToken);

        // 4) 레이어 전체 삭제로 변경 피드에 기록 + NOTIFY(다른 세션이 새로 고침)
        await using var changeTransaction = await connection.BeginTransactionAsync(cancellationToken);
        await ChangeFeedRepository.RecordAsync(
            connection, changeTransaction, changeContext,
            affectedLayers.Select(name => new ChangeEntry(name, ChangeEntityType.Import, null, ChangeOperation.Delete)).ToList(),
            cancellationToken, layoutId);
        await changeTransaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes one whole layer (its features and block instances, whatever file they came from) in
    /// a single transaction, drops block definitions no instance uses any more, and records a
    /// whole-layer delete so other sessions refresh. <c>"(no layer)"</c> addresses rows without a layer.
    /// Returns the number of rows removed.
    /// </summary>
    /// <param name="layerName">삭제할 레이어 이름(대소문자 구분 비교). 레이어 없는 행은 "(no layer)".</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <param name="changeContext">변경자 정보(변경 피드용, null 가능).</param>
    /// <returns>삭제된 행 수(피처 + 블록 인스턴스). 0이면 변경 기록도 남기지 않는다.</returns>
    public async Task<int> DeleteLayerAsync(
        string layerName, CancellationToken cancellationToken = default, ChangeContext? changeContext = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1) 두 테이블에서 이 도면의 해당 레이어 행 삭제(테이블명은 코드 상수, 값은 파라미터)
        var removed = 0;
        foreach (var table in new[] { "spatial_features", "spatial_block_instances" })
        {
            await using var command = new NpgsqlCommand(
                $"DELETE FROM {table} WHERE layout_id = {layoutId} AND COALESCE(layer_name, '(no layer)') = $1;", connection, transaction);
            command.Parameters.AddWithValue(layerName);
            removed += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2) 어떤 인스턴스도 쓰지 않는 블록 정의 정리
        await using (var definitions = new NpgsqlCommand("""
            DELETE FROM block_definitions d
            WHERE NOT EXISTS (SELECT 1 FROM spatial_block_instances i WHERE i.definition_id = d.id);
            """, connection, transaction))
        {
            await definitions.ExecuteNonQueryAsync(cancellationToken);
        }

        // 3) 실제로 지운 것이 있을 때만 레이어 전체 삭제를 기록하고 같은 트랜잭션에서 커밋
        if (removed > 0)
        {
            await ChangeFeedRepository.RecordAsync(
                connection, transaction, changeContext,
                new List<ChangeEntry> { new(layerName, ChangeEntityType.Import, null, ChangeOperation.Delete) },
                cancellationToken, layoutId);
        }

        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    /// <summary>True when any feature or block instance already uses this layer name (case-insensitive).</summary>
    /// <param name="layerName">확인할 레이어 이름(대소문자 무시). "(no layer)"는 레이어 없는 행을 뜻한다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>이 도면에서 사용 중이면 true.</returns>
    public async Task<bool> LayerExistsAsync(string layerName, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS (SELECT 1 FROM spatial_features WHERE layout_id = @@L@@ AND lower(COALESCE(layer_name, '(no layer)')) = lower($1))
                OR EXISTS (SELECT 1 FROM spatial_block_instances WHERE layout_id = @@L@@ AND lower(COALESCE(layer_name, '(no layer)')) = lower($1));
            """;
        await using var command = dataSource.CreateCommand(Q(sql));
        command.Parameters.AddWithValue(layerName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Renames a layer in both tables in one transaction and records the old name as deleted and the
    /// new one as updated so other sessions refresh. Returns the number of rows renamed.
    /// </summary>
    /// <param name="oldName">현재 레이어 이름(대소문자 구분 비교, "(no layer)"는 레이어 없는 행).</param>
    /// <param name="newName">새 레이어 이름. 중복 여부 검사는 호출자가 <see cref="LayerExistsAsync"/>로 해야 한다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <param name="changeContext">변경자 정보(변경 피드용, null 가능).</param>
    /// <returns>이름이 바뀐 행 수(피처 + 블록 인스턴스). 0이면 변경 기록을 남기지 않는다.</returns>
    public async Task<int> RenameLayerAsync(
        string oldName, string newName, CancellationToken cancellationToken = default, ChangeContext? changeContext = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1) 두 테이블의 layer_name을 한 트랜잭션에서 변경(updated_at은 건드리지 않음)
        var renamed = 0;
        foreach (var table in new[] { "spatial_features", "spatial_block_instances" })
        {
            await using var command = new NpgsqlCommand(
                $"UPDATE {table} SET layer_name = $2 WHERE layout_id = {layoutId} AND COALESCE(layer_name, '(no layer)') = $1;", connection, transaction);
            command.Parameters.AddWithValue(oldName);
            command.Parameters.AddWithValue(newName);
            renamed += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2) 이전 이름은 삭제, 새 이름은 갱신으로 기록해 다른 세션이 두 레이어를 모두 새로 읽게 한다
        if (renamed > 0)
        {
            await ChangeFeedRepository.RecordAsync(
                connection, transaction, changeContext,
                new List<ChangeEntry>
                {
                    new(oldName, ChangeEntityType.Import, null, ChangeOperation.Delete),
                    new(newName, ChangeEntityType.Import, null, ChangeOperation.Update)
                },
                cancellationToken, layoutId);
        }

        await transaction.CommitAsync(cancellationToken);
        return renamed;
    }

    /// <summary>해당 원본 파일에서 가져온 데이터(피처 또는 블록 인스턴스)가 이 도면에 이미 있는지 확인한다.</summary>
    /// <param name="sourceFile">원본 파일 이름/경로(source_file 값).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>있으면 true.</returns>
    public async Task<bool> HasImportedFileAsync(string sourceFile, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM spatial_features
                WHERE layout_id = @@L@@ AND source_file = $1 AND feature_type IN ('cad_block', 'dxf_layer')
            ) OR EXISTS (
                SELECT 1 FROM spatial_block_instances
                WHERE layout_id = @@L@@ AND source_file = $1
            );
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(Q(sql), connection);
        command.Parameters.AddWithValue(sourceFile);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    /// <summary>특정 원본 파일에서 가져온 데이터를 레이어별로 집계한다(도형 수, 블록 인스턴스 수). 가져오기 검증/감사용.</summary>
    /// <param name="sourceFile">원본 파일 이름/경로(source_file 값).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>레이어명 순 목록. 레이어 없는 행은 "(no layer)".</returns>
    public async Task<IReadOnlyList<ImportLayerCount>> FindImportedCountsAsync(
        string sourceFile,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH entity_counts AS (
                SELECT COALESCE(layer_name, '(no layer)') AS layer_name, COUNT(*) AS entity_count
                FROM spatial_features f
                CROSS JOIN LATERAL ST_Dump(f.geom) d
                WHERE f.layout_id = @@L@@ AND f.source_file = $1
                  AND f.feature_type IN ('cad_block', 'dxf_layer')
                GROUP BY COALESCE(layer_name, '(no layer)')
            ),
            block_counts AS (
                SELECT COALESCE(layer_name, '(no layer)') AS layer_name, COUNT(*) AS block_count
                FROM spatial_block_instances
                WHERE layout_id = @@L@@ AND source_file = $1
                GROUP BY COALESCE(layer_name, '(no layer)')
            )
            SELECT COALESCE(e.layer_name, b.layer_name),
                   COALESCE(e.entity_count, 0),
                   COALESCE(b.block_count, 0)
            FROM entity_counts e
            FULL OUTER JOIN block_counts b ON b.layer_name = e.layer_name
            ORDER BY 1;
            """;

        await using var command = dataSource.CreateCommand(Q(sql));
        command.Parameters.AddWithValue(sourceFile);
        var counts = new List<ImportLayerCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new ImportLayerCount(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return counts;
    }

    /// <summary>
    /// 편집 가능한 레이어 객체(cad_block, dxf_layer 피처)를 모두 읽는다. 각 객체의 updated_at은
    /// 저장 시 낙관적 동시성 비교(<see cref="ApplyLayerEditsAsync"/>)의 기준값으로 쓰인다.
    /// </summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>id 순 객체 목록.</returns>
    public async Task<IReadOnlyList<EditableLayerObject>> FindEditableLayerObjectsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, feature_type, block_name, source_file, layer_name, geom, outer_geom, attributes, updated_at
            FROM spatial_features
            WHERE layout_id = @@L@@ AND feature_type IN ('cad_block', 'dxf_layer')
            ORDER BY id;
            """;

        await using var command = dataSource.CreateCommand(Q(sql));
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
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>id 순 객체 목록(FeatureType은 "block_instance", updated_at 포함).</returns>
    public async Task<IReadOnlyList<EditableLayerObject>> FindEditableBlockInstancesAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, block_name, source_file, layer_name, geom, outer_geom, attributes, updated_at
            FROM spatial_block_instances
            WHERE layout_id = @@L@@
            ORDER BY id;
            """;

        await using var command = dataSource.CreateCommand(Q(sql));
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
    /// <param name="updates">수정 목록: 행 id, 새 도형(Geometry)과 외곽(OuterGeometry; SRID 5186), 편집 진입 시 읽은 updated_at(ExpectedUpdatedAt),
    /// 블록 인스턴스 여부(true면 spatial_block_instances), 새 attributes(null이면 속성은 갱신하지 않음; 블록 배치 속성 동기화용).</param>
    /// <param name="inserts">추가 목록: 복제 원본(Source; 종류/블록명/원본파일/레이어/속성)과 새 도형·외곽. Source.FeatureType이 "block_instance"이면 인스턴스 테이블에 추가.</param>
    /// <param name="deletes">삭제 목록: 행 id, 읽은 시점의 updated_at, 블록 인스턴스 여부.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <param name="changeContext">변경자 정보(변경 피드용, null 가능).</param>
    /// <returns>충돌한 행 id 목록(다른 세션이 먼저 수정/삭제한 경우). 충돌이 없으면 빈 목록.</returns>
    public async Task<IReadOnlyList<long>> ApplyLayerEditsAsync(
        IReadOnlyList<(long Id, Geometry Geometry, Geometry OuterGeometry, DateTime ExpectedUpdatedAt, bool IsBlockInstance, IReadOnlyDictionary<string, string?>? Attributes)> updates,
        IReadOnlyList<(EditableLayerObject Source, Geometry Geometry, Geometry OuterGeometry)> inserts,
        IReadOnlyList<(long Id, DateTime ExpectedUpdatedAt, bool IsBlockInstance)> deletes,
        CancellationToken cancellationToken = default,
        ChangeContext? changeContext = null)
    {
        // 충돌 id와 변경 피드에 기록할 변경 목록을 모은다
        var conflictIds = new List<long>();
        var changes = new List<ChangeEntry>();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1) 수정: updated_at이 읽었을 때 값과 같을 때만 UPDATE(낙관적 동시성). 성공 시 updated_at 갱신
        foreach (var update in updates)
        {
            var table = update.IsBlockInstance ? "spatial_block_instances" : "spatial_features";
            // Block instances also persist their placement attributes (insert point / rotation),
            // so the stored attributes keep matching the moved geometry.
            var writeAttributes = update.Attributes is not null;
            await using var command = new NpgsqlCommand(
                writeAttributes
                    ? $"UPDATE {table} SET geom = $1, outer_geom = $2, attributes = $5, updated_at = CURRENT_TIMESTAMP WHERE id = $3 AND updated_at = $4 RETURNING layer_name;"
                    : $"UPDATE {table} SET geom = $1, outer_geom = $2, updated_at = CURRENT_TIMESTAMP WHERE id = $3 AND updated_at = $4 RETURNING layer_name;",
                connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = update.Geometry });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = update.OuterGeometry });
            command.Parameters.AddWithValue(update.Id);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = update.ExpectedUpdatedAt });
            if (writeAttributes)
            {
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Json.JsonSerializer.Serialize(update.Attributes) });
            }
            // RETURNING yields no row when the optimistic-concurrency guard matched nothing.
            var layer = await command.ExecuteScalarAsync(cancellationToken);
            if (layer is null)
            {
                conflictIds.Add(update.Id);
            }
            else
            {
                changes.Add(new ChangeEntry(layer as string ?? NoLayer, ChangeKind(update.IsBlockInstance), update.Id, ChangeOperation.Update));
            }
        }

        // 2) 추가: 새 행을 INSERT하고 발급된 id를 변경 피드에 기록(object_uid는 DB 기본값으로 새로 발급)
        foreach (var insert in inserts)
        {
            if (insert.Source.FeatureType == "block_instance")
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO spatial_block_instances
                        (source_file, block_name, layer_name, attributes, geom, outer_geom, layout_id)
                    VALUES
                        ($1, $2, $3, $4, $5, $6, @@L@@)
                    RETURNING id;
                    """.Replace("@@L@@", layoutId.ToString()), connection, transaction);
                command.Parameters.AddWithValue((object?)insert.Source.SourceFile ?? DBNull.Value);
                command.Parameters.AddWithValue((object?)insert.Source.BlockName ?? DBNull.Value);
                command.Parameters.AddWithValue((object?)insert.Source.LayerName ?? DBNull.Value);
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Json.JsonSerializer.Serialize(insert.Source.Attributes) });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = insert.Geometry });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = insert.OuterGeometry });
                var newId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
                changes.Add(new ChangeEntry(insert.Source.LayerName ?? NoLayer, ChangeKind(true), newId, ChangeOperation.Insert));
                continue;
            }

            await using (var command = new NpgsqlCommand("""
                INSERT INTO spatial_features
                    (feature_type, block_name, source_file, layer_name, attributes, geom, outer_geom, layout_id)
                VALUES
                    ($1, $2, $3, $4, $5, $6, $7, @@L@@)
                RETURNING id;
                """.Replace("@@L@@", layoutId.ToString()), connection, transaction))
            {
                command.Parameters.AddWithValue(insert.Source.FeatureType);
                command.Parameters.AddWithValue((object?)insert.Source.BlockName ?? DBNull.Value);
                command.Parameters.AddWithValue((object?)insert.Source.SourceFile ?? DBNull.Value);
                command.Parameters.AddWithValue((object?)insert.Source.LayerName ?? DBNull.Value);
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Json.JsonSerializer.Serialize(insert.Source.Attributes) });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = insert.Geometry });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Geometry, Value = insert.OuterGeometry });
                var newId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
                changes.Add(new ChangeEntry(insert.Source.LayerName ?? NoLayer, ChangeKind(false), newId, ChangeOperation.Insert));
            }
        }

        // 3) 삭제: 수정과 같은 updated_at 가드. 0행이면 이미 다른 세션이 바꾸거나 지운 것이므로 충돌로 보고
        foreach (var delete in deletes)
        {
            var table = delete.IsBlockInstance ? "spatial_block_instances" : "spatial_features";
            await using var command = new NpgsqlCommand(
                $"DELETE FROM {table} WHERE id = $1 AND updated_at = $2 RETURNING layer_name;", connection, transaction);
            command.Parameters.AddWithValue(delete.Id);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = delete.ExpectedUpdatedAt });
            var layer = await command.ExecuteScalarAsync(cancellationToken);
            if (layer is null)
            {
                conflictIds.Add(delete.Id);
            }
            else
            {
                changes.Add(new ChangeEntry(layer as string ?? NoLayer, ChangeKind(delete.IsBlockInstance), delete.Id, ChangeOperation.Delete));
            }
        }

        // 4) 블록 정의/변환 컬럼 동기화 → 라이브러리 스키마 보장 → 성공한 변경만 변경 피드 기록(NOTIFY) → 커밋
        await SyncBlockDefinitionsAsync(connection, transaction, cancellationToken);
        await LibraryRepository.EnsureSchemaAsync(connection, transaction, cancellationToken);
        await ChangeFeedRepository.RecordAsync(connection, transaction, changeContext, changes, cancellationToken, layoutId);
        await transaction.CommitAsync(cancellationToken);
        return conflictIds;
    }

    /// <summary>레이어명이 없는(NULL) 객체를 변경 피드에 기록할 때 쓰는 표시 이름.</summary>
    private const string NoLayer = "(no layer)";

    /// <summary>변경 피드의 entity_type 값을 결정한다.</summary>
    /// <param name="isBlockInstance">블록 인스턴스 테이블의 행이면 true.</param>
    /// <returns>블록 인스턴스면 <c>ChangeEntityType.BlockInstance</c>, 아니면 <c>ChangeEntityType.Feature</c>.</returns>
    private static string ChangeKind(bool isBlockInstance) =>
        isBlockInstance ? ChangeEntityType.BlockInstance : ChangeEntityType.Feature;

    /// <summary>블록 인스턴스 다중 행 INSERT의 한 배치 크기(행 수). 500행 x 6 파라미터 = 3000개 파라미터로 PostgreSQL 한도(65535) 이내.</summary>
    private const int InstanceBatchSize = 500;

    /// <summary>
    /// Writes an entire DXF import (layer rows + block instances) in one transaction. Block
    /// instances are written via multi-row INSERT batches instead of one round trip per row,
    /// which matters once a layer has hundreds of instances (SmartLayout.dxf's Equipment layer
    /// alone has 126). Reports progress after each layer/batch and honors cancellation between
    /// statements; an exception or cancellation rolls the whole import back so no partial data
    /// is left behind.
    /// </summary>
    /// <param name="layers">저장할 레이어 행: (블록명, 레이어명(null 가능), 도형, 외곽 도형(SRID 5186), 속성, 피처 종류 'cad_block'/'dxf_layer').</param>
    /// <param name="instances">저장할 블록 인스턴스: (블록명, 레이어명(null 가능), 도형, 외곽 도형(SRID 5186), 속성).</param>
    /// <param name="sourceFile">원본 DXF 파일 이름/경로(모든 행의 source_file로 기록; null 불가).</param>
    /// <param name="progress">진행 보고 수신자(null 가능). 레이어/배치 하나가 끝날 때마다 (단계명, 완료 수, 전체 수) 보고.</param>
    /// <param name="cancellationToken">취소 토큰. 취소되면 예외가 발생하고 전체가 롤백된다.</param>
    /// <param name="changeContext">변경자 정보(변경 피드용, null 가능).</param>
    public async Task ImportAsync(
        IReadOnlyList<(string BlockName, string? LayerName, Geometry Geometry, Geometry OuterGeometry, IReadOnlyDictionary<string, string?> Attributes, string FeatureType)> layers,
        IReadOnlyList<(string BlockName, string? LayerName, Geometry Geometry, Geometry OuterGeometry, IReadOnlyDictionary<string, string?> Attributes)> instances,
        string sourceFile,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default,
        ChangeContext? changeContext = null)
    {
        // 진행률 분모/분자: 레이어 행 + 블록 인스턴스 전체 건수
        var total = layers.Count + instances.Count;
        var completed = 0;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1) 레이어 행을 한 건씩 INSERT(건수가 적음). 매 건마다 취소 확인 후 진행 보고
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var command = new NpgsqlCommand("""
                INSERT INTO spatial_features
                    (feature_type, block_name, source_file, layer_name, attributes, geom, outer_geom, layout_id)
                VALUES
                    ($1, $2, $3, $4, $5, $6, $7, @@L@@);
                """.Replace("@@L@@", layoutId.ToString()), connection, transaction))
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

        // 2) 블록 인스턴스는 500건씩 다중 행 INSERT(파라미터는 행당 6개: 파일, 블록명, 레이어, 속성, 도형, 외곽)
        for (var offset = 0; offset < instances.Count; offset += InstanceBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchCount = Math.Min(InstanceBatchSize, instances.Count - offset);
            await using (var command = new NpgsqlCommand(BuildInstanceBatchInsertSql(batchCount, layoutId), connection, transaction))
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

        // 3) 변환 컬럼/블록 정의 동기화 → 라이브러리 스키마 보장 → 영향 레이어(대소문자 무시 중복 제거)를
        //    레이어 전체 INSERT로 변경 피드에 기록(NOTIFY) → 커밋. 중간에 예외/취소가 나면 커밋되지 않아 전체 롤백
        await SyncBlockDefinitionsAsync(connection, transaction, cancellationToken);
        await LibraryRepository.EnsureSchemaAsync(connection, transaction, cancellationToken);
        await ChangeFeedRepository.RecordAsync(
            connection, transaction, changeContext,
            layers.Select(layer => layer.LayerName).Concat(instances.Select(instance => instance.LayerName))
                .Select(name => name ?? NoLayer).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new ChangeEntry(name, ChangeEntityType.Import, null, ChangeOperation.Insert)).ToList(),
            cancellationToken, layoutId);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>블록 인스턴스 다중 행 INSERT SQL을 만든다. 행당 위치 파라미터 6개($1~$6, $7~$12, ...)와 layout_id 리터럴을 쓴다.</summary>
    /// <param name="rowCount">한 문장에 넣을 행 수(1 이상, <see cref="InstanceBatchSize"/> 이하).</param>
    /// <param name="layoutId">모든 행에 기록할 도면 ID(숫자 리터럴로 삽입).</param>
    /// <returns>"INSERT INTO spatial_block_instances (...) VALUES (...), (...);" 문자열.</returns>
    private static string BuildInstanceBatchInsertSql(int rowCount, long layoutId)
    {
        var sql = new StringBuilder(
            "INSERT INTO spatial_block_instances (source_file, block_name, layer_name, attributes, geom, outer_geom, layout_id) VALUES ");
        for (var i = 0; i < rowCount; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            var p = i * 6;
            sql.Append($"(${p + 1}, ${p + 2}, ${p + 3}, ${p + 4}, ${p + 5}, ${p + 6}, {layoutId})");
        }

        sql.Append(';');
        return sql.ToString();
    }

    /// <summary>내부 데이터 소스(연결 풀)를 해제한다.</summary>
    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}