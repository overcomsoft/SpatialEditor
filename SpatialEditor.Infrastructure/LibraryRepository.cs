// ============================================================================
// LibraryRepository.cs : 전역 설비 블록 라이브러리 저장소 (PostgreSQL + PostGIS, Npgsql)
//
// [사용 테이블]
//   block_categories          분류 트리(parent_id 자기참조, (부모, 이름) 고유)
//   block_library             라이브러리 항목(code 고유, 상태, 현재 버전 번호, is_deleted 소프트 삭제)
//   block_library_versions    항목의 버전별 로컬 좌표 지오메트리 + shape_hash(형상 중복 판정용)
//   block_ports               버전별 포트(연결점) - 이 파일에서는 테이블만 생성
//   spatial_block_instances   도면에 배치된 블록 인스턴스(library_id 컬럼을 여기서 추가/연결)
//   block_definitions         DXF 가져오기 때 생긴 블록 정의(최초 이관의 원본)
//
// [흐름]
//   EnsureSchemaAsync : 테이블/인덱스 생성 -> 기본 분류('미분류') 시드(빈 트리일 때만)
//        -> block_definitions 를 블록 이름당 1개 라이브러리 항목(+버전 1)으로 이관(형상 해시로 중복 제외)
//        -> 인스턴스를 (1) 같은 형상 (2) 같은 블록 이름 순으로 라이브러리에 연결.  여러 번 실행해도 안전(멱등)
//   조회/편집 : ListCategories/AddCategory/RenameCategory, ListItems, UpdateItem(카탈로그 필드만), DeleteItem(소프트 삭제)
//   RegisterFromInstanceAsync : 도면에 배치된 인스턴스의 모양을 블록 자신의 좌표계로 되돌려
//        형상 해시 계산 -> 같은 형상이 있으면 기존 항목에 연결, 없으면 새 항목 + 버전 1 생성
//        -> 같은 정의를 쓰는 다른 인스턴스도 모두 연결.  (전부 한 트랜잭션)
//
// 형상 해시: 좌표를 1mm(0.001) 격자로 스냅한 WKT 의 md5. 1mm 이내 차이는 같은 블록으로 취급한다.
// 도메인 모델은 SpatialEditor.Domain/Library.cs 참고.
// ============================================================================
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using SpatialEditor.Domain;

namespace SpatialEditor.Infrastructure;

/// <summary>
/// 전역 설비 블록 라이브러리 저장소입니다: 분류 트리, 버전별 로컬 좌표 지오메트리를 가진 라이브러리 항목,
/// 그리고 도면에 배치된 블록 인스턴스와 라이브러리 항목 사이의 연결을 다룹니다.
/// 사용이 끝나면 <see cref="DisposeAsync"/> 로 연결 풀을 해제해야 합니다.
/// </summary>
public sealed class LibraryRepository : IAsyncDisposable
{
    /// <summary>기본 분류 이름. 분류 트리가 비어 있을 때 최상위 분류로 한 번 시드됩니다.</summary>
    public const string UncategorizedName = "미분류";

    // 형상 해시 SQL 템플릿입니다. {0} 자리에 지오메트리 식을 넣습니다.
    // 좌표를 0.001(미터, 즉 1mm) 격자에 맞춘 뒤 WKT 의 md5 를 구하므로, 1mm 이내 차이는 같은 블록으로 판정됩니다.
    private const string HashOf = "md5(ST_AsText(ST_SnapToGrid({0}, 0.001)))";

    /// <summary>연결 풀을 관리하는 Npgsql 데이터 소스(NetTopologySuite 형식 매핑 사용).</summary>
    private readonly NpgsqlDataSource dataSource;

    /// <summary>저장소를 만들고 NetTopologySuite 매핑이 켜진 연결 풀을 준비합니다(이 시점에는 DB 에 접속하지 않습니다).</summary>
    /// <param name="connectionString">PostgreSQL 연결 문자열. null/빈 값이면 안 됩니다.</param>
    public LibraryRepository(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseNetTopologySuite();
        dataSource = builder.Build();
    }

    /// <summary>연결 풀을 해제합니다.</summary>
    /// <returns>해제 작업을 나타내는 ValueTask.</returns>
    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    /// <summary>
    /// 라이브러리 테이블을 만들고, 기존 <c>block_definitions</c> 를 라이브러리 항목으로 이관합니다
    /// (블록 이름당 1개, 형상 해시로 중복 제거). 이어서 배치된 인스턴스를 항목에 연결합니다. 여러 번 실행해도 안전(멱등)합니다.
    /// </summary>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>작업 완료를 나타내는 Task.</returns>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);
    }

    /// <summary>
    /// 스키마 생성과 이관을 실제로 수행하는 내부 구현입니다. 다른 저장소가 자신의 트랜잭션 안에서
    /// 함께 호출할 수 있도록 연결/트랜잭션을 외부에서 받습니다.
    /// </summary>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">소속 트랜잭션. 트랜잭션 없이 실행하려면 null.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>작업 완료를 나타내는 Task.</returns>
    internal static async Task EnsureSchemaAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        // 1) 형상 해시 식을 SQL 에 끼워 넣을 수 있게 미리 문자열로 만듭니다.
        //    (후보 = 이관 대상 정의 s, 정의 = 인스턴스가 가리키는 정의 d)
        var hashOfDefinition = string.Format(HashOf, "d.local_geom");
        var hashOfCandidate = string.Format(HashOf, "s.local_geom");
        // 2) 하나의 SQL 배치로 다음을 순서대로 수행합니다(모두 IF NOT EXISTS/WHERE NOT EXISTS 라 멱등).
        //    a) 테이블/인덱스 생성  b) 기본 분류 시드(빈 트리일 때만)
        //    c) block_definitions 를 블록 이름당 1개 항목 + 버전 1 로 이관(같은 code 이거나 같은 형상이면 건너뜀)
        //    d) 인스턴스 연결: 먼저 형상이 같은 항목, 그다음 블록 이름이 코드와 같은 항목
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

        // 3) 위 배치를 실행합니다.
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>모든 분류를 이름순으로 조회합니다. 트리 구성은 호출자가 ParentId 로 합니다.</summary>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>평면 분류 목록.</returns>
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

    /// <summary>분류를 추가합니다.</summary>
    /// <param name="parentId">상위 분류 id. 최상위로 만들려면 null.</param>
    /// <param name="name">분류 이름(앞뒤 공백 제거). 비어 있으면 안 됩니다.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>새 분류의 id.</returns>
    /// <exception cref="InvalidOperationException">이름이 비었거나 같은 위치에 같은 이름이 이미 있을 때.</exception>
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
        // ON CONFLICT DO NOTHING 이라 같은 부모 아래 같은 이름이면 삽입이 무시되어 id 가 돌아오지 않습니다. 이것으로 중복을 감지합니다.
        var id = await command.ExecuteScalarAsync(cancellationToken);
        if (id is null)
        {
            throw new InvalidOperationException($"Category '{name}' already exists here.");
        }

        return (long)id;
    }

    /// <summary>분류 이름을 바꿉니다.</summary>
    /// <param name="id">대상 분류 id.</param>
    /// <param name="name">새 이름(앞뒤 공백 제거). 비어 있으면 안 됩니다.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <exception cref="InvalidOperationException">이름이 비었음, 분류가 없음, 같은 단계에 같은 이름이 이미 있음.</exception>
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
        // (부모, 이름) 고유 인덱스 위반을 사용자용 메시지로 바꿉니다.
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"A category named '{name}' already exists at this level.");
        }
    }

    /// <summary>
    /// 삭제되지 않은 모든 라이브러리 항목을 코드 순으로 조회합니다. 분류 이름, 배치된 인스턴스 수,
    /// 현재 버전의 로컬 지오메트리를 함께 가져옵니다.
    /// </summary>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>라이브러리 항목 목록.</returns>
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
    /// 도면에 배치된 블록 인스턴스의 형상을 새 라이브러리 항목(버전 1)으로 등록하고 그 인스턴스를 연결합니다.
    /// 라이브러리에 같은 형상이 이미 있으면 새로 만들지 않고 그 항목에 인스턴스를 연결합니다.
    /// </summary>
    /// <param name="instanceId">등록할 블록 인스턴스(spatial_block_instances)의 id.</param>
    /// <param name="code">새 항목 코드. 공백 제거 후 비어 있으면 안 되고 기존 코드와 겹치면 안 됩니다.</param>
    /// <param name="name">새 항목 표시 이름. 공백 제거 후 비어 있으면 안 됩니다.</param>
    /// <param name="categoryId">분류 id. null 이면 최상위 첫 분류(기본 분류)로 들어갑니다.</param>
    /// <param name="vendor">제조사. 비어 있거나 공백이면 null 로 저장.</param>
    /// <param name="modelNo">모델 번호. 비어 있거나 공백이면 null 로 저장.</param>
    /// <param name="userId">작업한 사용자 id(생성자/수정자 기록). 알 수 없으면 null.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>새로 만들었는지(Created) 기존 항목에 연결했는지(LinkedToExisting)와 항목 id/코드.</returns>
    /// <exception cref="InvalidOperationException">필수값 누락, 인스턴스 없음/배치 정보 없음, 이미 연결됨, 코드 중복.</exception>
    public async Task<LibraryRegisterResult> RegisterFromInstanceAsync(
        long instanceId, string code, string name, long? categoryId, string? vendor, string? modelNo,
        long? userId, CancellationToken cancellationToken = default)
    {
        // 1) 입력 정리: 앞뒤 공백을 제거하고 코드/이름이 비어 있으면 거부합니다.
        code = code.Trim();
        name = name.Trim();
        if (code.Length == 0 || name.Length == 0)
        {
            throw new InvalidOperationException("Code and name are required.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 2) 인스턴스의 지오메트리를 블록 자신의 좌표계(삽입점이 원점, 회전/배율 없음)로 되돌리는 SQL 식입니다.
        //    배치 시 적용된 이동/회전/배율 변환의 역변환이며, 정의 동기화와 같은 수식입니다.
        const string localGeom = """
            ST_Affine(ST_Translate({0}, -insert_x, -insert_y),
                cos(radians(rotation_deg)) / scale_x, sin(radians(rotation_deg)) / scale_x,
                -sin(radians(rotation_deg)) / scale_y, cos(radians(rotation_deg)) / scale_y, 0, 0)
            """;
        // 실제 도형(geom)과 외곽(outer_geom) 각각에 같은 역변환 식을 적용합니다.
        var geomExpr = string.Format(localGeom, "geom");
        var outerExpr = string.Format(localGeom, "outer_geom");

        // 3) 프로브 쿼리: 이미 연결된 라이브러리 id, 로컬 형상 해시, 외곽 상자의 가로(width)/세로(depth)를 한 번에 구합니다.
        //    삽입점이 없거나 배율이 0 인 인스턴스는 역변환이 불가능하므로 조회에서 제외되어 "배치 정보 없음" 오류가 됩니다.
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

        // 4) 이미 라이브러리 항목에 연결된 인스턴스는 다시 등록하지 않습니다(어느 항목인지 알려줌).
        if (existingLibraryId is not null)
        {
            await using var named = new NpgsqlCommand("SELECT code FROM block_library WHERE id = $1;", connection, transaction);
            named.Parameters.AddWithValue(existingLibraryId.Value);
            var existingCode = (string?)await named.ExecuteScalarAsync(cancellationToken);
            throw new InvalidOperationException($"This block is already linked to library item '{existingCode}'.");
        }

        // 5) 중복 감지: 같은 형상 해시를 현재 버전으로 가진(삭제되지 않은) 항목이 있으면
        //    새로 만들지 않고 그 항목에 인스턴스만 연결한 뒤 커밋하고 끝냅니다.
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

        // 6) 새 항목을 만들기 전에 코드 중복을 확인합니다(삭제된 항목의 코드도 포함하여 UNIQUE 위반을 피함).
        await using (var codeCheck = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM block_library WHERE code = $1);", connection, transaction))
        {
            codeCheck.Parameters.AddWithValue(code);
            if ((bool)(await codeCheck.ExecuteScalarAsync(cancellationToken))!)
            {
                throw new InvalidOperationException($"A library item with code '{code}' already exists.");
            }
        }

        // 7) 라이브러리 항목 삽입: 분류가 없으면 최상위 첫 분류를 사용하고, 크기(width/depth)는 3)에서 구한 값을 씁니다.
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

        // 8) 버전 1 삽입: 3)과 같은 역변환 식으로 로컬 지오메트리를 만들어 저장하고 형상 해시를 함께 기록합니다.
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

        // 9) 원본 정의를 공유하는 인스턴스는 모두 같은 블록이므로, 아직 연결되지 않은 것들을 한꺼번에 연결합니다.
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

    /// <summary>블록 인스턴스 하나를 라이브러리 항목에 연결합니다(library_id 설정).</summary>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">소속 트랜잭션.</param>
    /// <param name="instanceId">연결할 인스턴스 id.</param>
    /// <param name="libraryId">연결 대상 라이브러리 항목 id.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    private static async Task LinkInstanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, long libraryId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE spatial_block_instances SET library_id = $1 WHERE id = $2;", connection, transaction);
        command.Parameters.AddWithValue(libraryId);
        command.Parameters.AddWithValue(instanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>라이브러리 항목의 카탈로그 필드(이름, 분류, 제조사, 모델, 설명, 높이, 상태)를 수정합니다. 지오메트리는 바뀌지 않습니다.</summary>
    /// <param name="id">수정할 항목 id.</param>
    /// <param name="edit">새 값 묶음. 이름은 필수이고 상태는 <see cref="LibraryStatus.All"/> 중 하나여야 합니다.</param>
    /// <param name="userId">수정한 사용자 id. 알 수 없으면 null.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <exception cref="InvalidOperationException">이름 누락, 알 수 없는 상태, 항목이 없거나 삭제됨.</exception>
    public async Task UpdateItemAsync(long id, LibraryItemEdit edit, long? userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(edit.Name))
        {
            throw new InvalidOperationException("Enter a name.");
        }

        // 상태 문자열은 DB 에 그대로 저장되므로 허용된 값인지 먼저 검사합니다.
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

    /// <summary>
    /// 라이브러리 항목을 소프트 삭제(is_deleted=TRUE)합니다. 배치된 블록이 아직 사용 중인 항목은 삭제할 수 없으며
    /// 대신 상태를 '사용 중단(deprecated)'으로 바꿔야 합니다.
    /// </summary>
    /// <param name="id">삭제할 항목 id.</param>
    /// <param name="userId">삭제한 사용자 id. 알 수 없으면 null.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <exception cref="InvalidOperationException">도면에서 아직 사용 중일 때.</exception>
    public async Task DeleteItemAsync(long id, long? userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var usage = new NpgsqlCommand("SELECT count(*) FROM spatial_block_instances WHERE library_id = $1;", connection);
        usage.Parameters.AddWithValue(id);
        // 사용 중인 인스턴스 수를 확인해, 있으면 삭제를 거부합니다(도면의 참조가 끊어지는 것을 방지).
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

    /// <summary>빈 문자열/공백뿐인 값은 null 로, 그 외에는 앞뒤 공백을 제거해 돌려줍니다(DB 에 빈 문자열 대신 NULL 저장).</summary>
    /// <param name="value">입력 문자열(null 가능).</param>
    /// <returns>정리된 문자열 또는 null.</returns>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
