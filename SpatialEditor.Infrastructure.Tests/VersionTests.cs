using NetTopologySuite;
using NetTopologySuite.Geometries;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

// ============================================================================
// SpatialEditor.Infrastructure.Tests / VersionTests : 버전 비교(VersionDiffer)와 버전/도면 저장소(VersionRepository) 테스트
// ----------------------------------------------------------------------------
// [구성]
//   FactIfDatabaseAttribute       : 환경 변수 SPATIAL_TEST_CONNECTION 이 설정되어 있을 때만 실행되는 [Fact].
//                                   비어 있으면 Skip 사유를 붙여 건너뛴다(CI/개발 PC 에 DB 가 없어도 빌드/테스트 통과).
//   VersionDifferTests            : DB 없이 순수 메모리로 VersionDiffer.Compare 규칙을 검증.
//   VersionRepositoryTests        : 실제 PostgreSQL/PostGIS 에 접속하는 통합 테스트.
// [실행 조건(통합 테스트)]
//   환경 변수 SPATIAL_TEST_CONNECTION 에 "빈" PostgreSQL/PostGIS 테스트 데이터베이스의 연결 문자열을 지정해야 한다.
//   각 테스트는 시작할 때 ResetAsync 로 앱 테이블들(layouts, spatial_* , layout_version_* , change_log 등)을
//   DROP TABLE ... CASCADE 하므로 운영/개발 데이터가 있는 DB 를 절대 지정하면 안 된다.
// [VersionDifferTests 흐름] Block(...) 헬퍼로 10x10 사각형 블록 항목을 만들고 Compare(a, b) 로 두 상태를 비교
//   · 동일 상태 → 차이 없음
//   · 같은 UID 기준으로 추가/삭제 판별
//   · 이동(Moved)과 회전(Rotated)은 따로 보고
//   · 아주 작은 위치 오차/각도 되감기(359.99999999 vs 0)는 변경이 아님, AngleDifference(350,10)=20
//   · 이름/레이어 변경은 Reshaped, 속성 변경은 AttributesChanged 로 보고
//   · 배치 속성(block_insert_x, block_rotation_deg 등)만 달라도 속성 변경으로 치지 않음
// [VersionRepositoryTests 흐름]
//   1) VersionLifecycle_SaveCompareRestoreBranchBaselineDelete : 설비 2개 가져오기 → v1 저장(동일하면 저장 거부)
//      → 한 개 이동 후 작업본과 v1 비교(이동 1건) → v2 저장 → v1 복원(작업본 = v1, UID 유지)
//      → 편집 후 v2 로 복원하면 복원 전 상태가 pre-restore 버전으로 보존 → 분기 도면 생성/독립 편집
//      → 기준선 보호/이동, 삭제 시 부모 재연결 → 도면 삭제(마지막 도면은 삭제 불가)
//   2) ExistingDatabaseWithoutLayoutsIsMigratedAndGetsUniqueUids : 버전 관리 이전 스키마로 되돌린 DB 를
//      EnsureSchemaAsync 가 제자리에서 업그레이드하고 각 객체에 고유 UID 를 부여하는지
//   3) EditsAndLayerOperationsStayInsideTheirLayout : 한 도면에서의 레이어 이름 변경/삭제가 다른 도면에 영향이 없고,
//      변경 로그도 도면별로 분리되는지
// ============================================================================
namespace SpatialEditor.Infrastructure.Tests;

/// <summary>
/// 환경 변수 SPATIAL_TEST_CONNECTION 이 "빈" PostgreSQL/PostGIS 테스트 DB 를 가리킬 때만 실행되는 [Fact] 특성.
/// 변수가 비어 있으면 Skip 사유가 설정되어 테스트가 건너뛰어진다(DB 가 없는 환경에서도 테스트 실행이 실패하지 않음).
/// </summary>
public sealed class FactIfDatabaseAttribute : FactAttribute
{
    /// <summary>테스트 DB 연결 문자열을 담는 환경 변수 이름.</summary>
    public const string Variable = "SPATIAL_TEST_CONNECTION";

    /// <summary>환경 변수가 설정되어 있지 않으면 Skip 사유를 지정해 테스트를 건너뛰게 한다.</summary>
    public FactIfDatabaseAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to an empty PostGIS test database to run this test.";
        }
    }
}

/// <summary>VersionDiffer.Compare 의 비교 규칙(추가/삭제/이동/회전/속성 변경, 허용오차)을 DB 없이 검증하는 테스트 모음.</summary>
public class VersionDifferTests
{
    /// <summary>SRID 5186 으로 도형을 만드는 팩토리.</summary>
    private static readonly GeometryFactory Factory = NtsGeometryServices.Instance.CreateGeometryFactory(5186);

    /// <summary>비교 테스트용 블록 항목을 만든다. 삽입점(x,y)에서 10x10 정사각형 도형을 가진다.</summary>
    /// <param name="uid">객체 고유 식별자(두 상태에서 같은 UID 면 같은 객체로 본다).</param>
    /// <param name="name">블록 이름.</param>
    /// <param name="x">삽입점 X.</param>
    /// <param name="y">삽입점 Y.</param>
    /// <param name="rot">회전각(도).</param>
    /// <param name="layer">레이어 이름.</param>
    /// <param name="scale">X/Y 배율.</param>
    /// <param name="attributes">추가 속성(null 이면 빈 사전).</param>
    /// <returns>생성된 블록 항목.</returns>
    private static VersionBlockItem Block(Guid uid, string name, double x, double y, double rot = 0, string layer = "Equipment",
        double scale = 1, Dictionary<string, string?>? attributes = null)
    {
        var square = Factory.CreatePolygon(new[]
        {
            new Coordinate(x, y), new Coordinate(x + 10, y), new Coordinate(x + 10, y + 10), new Coordinate(x, y + 10), new Coordinate(x, y)
        });
        return new VersionBlockItem(uid, name, layer, "f.dxf", x, y, rot, scale, scale, attributes ?? new Dictionary<string, string?>(), square, square);
    }

    /// <summary>블록 목록 두 개(이전/이후)를 비교한다(일반 객체 목록은 비어 있음).</summary>
    /// <param name="a">이전 상태의 블록들.</param>
    /// <param name="b">이후 상태의 블록들.</param>
    /// <returns>비교 결과.</returns>
    private static VersionDiff Compare(IEnumerable<VersionBlockItem> a, IEnumerable<VersionBlockItem> b) =>
        VersionDiffer.Compare(a.ToList(), b.ToList(), Array.Empty<VersionFeatureItem>(), Array.Empty<VersionFeatureItem>());

    /// <summary>완전히 같은 두 상태는 차이가 없다(IsEmpty)는 것을 검증한다.</summary>
    [Fact]
    public void IdenticalStatesHaveNoDifferences()
    {
        var uid = Guid.NewGuid();
        Assert.True(Compare(new[] { Block(uid, "A", 0, 0) }, new[] { Block(uid, "A", 0, 0) }).IsEmpty);
    }

    /// <summary>UID 를 기준으로 새로 생긴 객체는 추가, 사라진 객체는 삭제로 보고되고 유지된 객체는 차이가 없음을 검증한다.</summary>
    [Fact]
    public void AddedAndRemovedObjectsAreFoundByUid()
    {
        var kept = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var added = Guid.NewGuid();
        var diff = Compare(new[] { Block(kept, "A", 0, 0), Block(removed, "B", 50, 0) }, new[] { Block(kept, "A", 0, 0), Block(added, "C", 100, 0) });

        Assert.Equal(1, diff.Count(DiffKind.Added));
        Assert.Equal(1, diff.Count(DiffKind.Removed));
        Assert.Equal(2, diff.Entries.Count);
    }

    /// <summary>위치만 바뀌면 이동(Moved), 각도만 바뀌면 회전(Rotated)으로 따로 보고되며 추가는 0건임을 검증한다.</summary>
    [Fact]
    public void MoveAndRotationAreReportedSeparately()
    {
        var uid = Guid.NewGuid();
        var diff = Compare(new[] { Block(uid, "A", 0, 0, 0) }, new[] { Block(uid, "A", 5, 0, 90) });

        Assert.Equal(1, diff.Count(DiffKind.Moved));
        Assert.Equal(1, diff.Count(DiffKind.Rotated));
        Assert.Equal(0, diff.Count(DiffKind.Added));
    }

    /// <summary>아주 작은 위치 오차와 각도 되감기(359.99999999도 vs 0도)는 변경으로 보지 않고, AngleDifference(350,10)이 20도임을 검증한다.</summary>
    [Fact]
    public void TinyPositionNoiseAndAngleWrapAroundAreNotChanges()
    {
        var uid = Guid.NewGuid();
        var diff = Compare(new[] { Block(uid, "A", 0, 0, 359.99999999) }, new[] { Block(uid, "A", 0.00001, 0, 0) });
        Assert.True(diff.IsEmpty);
        Assert.Equal(20, VersionDiffer.AngleDifference(350, 10), 6);
    }

    /// <summary>블록 이름/레이어가 바뀌면 교체(Reshaped), 일반 속성 값이 바뀌면 속성 변경(AttributesChanged)으로 각각 1건 보고됨을 검증한다.</summary>
    [Fact]
    public void ChangedBlockNameLayerAndAttributesAreReported()
    {
        var uid = Guid.NewGuid();
        var a = Block(uid, "A", 0, 0, attributes: new Dictionary<string, string?> { ["owner"] = "kim", ["block_insert_x"] = "0" });
        var b = Block(uid, "B", 0, 0, layer: "Other", attributes: new Dictionary<string, string?> { ["owner"] = "lee", ["block_insert_x"] = "0" });
        var diff = Compare(new[] { a }, new[] { b });

        Assert.Equal(1, diff.Count(DiffKind.Reshaped));
        Assert.Equal(1, diff.Count(DiffKind.AttributesChanged));
    }

    /// <summary>배치 속성(block_insert_x, block_rotation_deg)만 달라진 경우는 속성 변경으로 치지 않음을 검증한다(위치/회전은 별도 규칙).</summary>
    [Fact]
    public void PlacementAttributesAloneAreNotAttributeChanges()
    {
        var uid = Guid.NewGuid();
        var a = Block(uid, "A", 0, 0, attributes: new Dictionary<string, string?> { ["block_insert_x"] = "0", ["block_rotation_deg"] = "0" });
        var b = Block(uid, "A", 0, 0, attributes: new Dictionary<string, string?> { ["block_insert_x"] = "5", ["block_rotation_deg"] = "90" });
        Assert.True(Compare(new[] { a }, new[] { b }).IsEmpty);
    }
}

/// <summary>
/// VersionRepository 통합 테스트(실제 PostgreSQL/PostGIS 필요). 환경 변수 SPATIAL_TEST_CONNECTION 이 가리키는 빈 DB 에서만 실행되며,
/// 각 테스트 시작 시 관련 테이블을 모두 DROP 하므로 데이터가 있는 DB 를 지정하면 안 된다.
/// </summary>
public class VersionRepositoryTests
{
    /// <summary>SRID 5186 으로 도형을 만드는 팩토리.</summary>
    private static readonly GeometryFactory Factory = NtsGeometryServices.Instance.CreateGeometryFactory(5186);

    /// <summary>환경 변수에서 읽은 테스트 DB 연결 문자열(FactIfDatabase 가 설정 여부를 보장).</summary>
    private static string Connection => Environment.GetEnvironmentVariable(FactIfDatabaseAttribute.Variable)!;

    /// <summary>(x,y)에서 시작하는 10x10 정사각형 폴리곤을 만든다.</summary>
    /// <param name="x">왼쪽 아래 X.</param>
    /// <param name="y">왼쪽 아래 Y.</param>
    /// <returns>정사각형 도형.</returns>
    private static Geometry Square(double x, double y) => Factory.CreatePolygon(new[]
    {
        new Coordinate(x, y), new Coordinate(x + 10, y), new Coordinate(x + 10, y + 10), new Coordinate(x, y + 10), new Coordinate(x, y)
    });

    /// <summary>가져오기용 블록 배치 속성(이름 EQ, 삽입 좌표, 회전 0, 배율 1)을 만든다.</summary>
    /// <param name="x">삽입점 X.</param>
    /// <param name="y">삽입점 Y.</param>
    /// <returns>속성 사전(숫자는 InvariantCulture 문자열).</returns>
    private static Dictionary<string, string?> Attributes(double x, double y) => new()
    {
        ["block_name"] = "EQ", ["block_insert_x"] = x.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
        ["block_insert_y"] = y.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
        ["block_rotation_deg"] = "0", ["block_scale_x"] = "1", ["block_scale_y"] = "1"
    };

    /// <summary>테스트 격리를 위해 앱이 사용하는 모든 테이블을 DROP TABLE ... CASCADE 로 지운다(빈 DB 상태에서 시작). 운영 DB 에서 실행 금지.</summary>
    private static async Task ResetAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            DROP TABLE IF EXISTS layout_version_blocks, layout_version_features, layout_versions, change_log,
                spatial_block_instances, spatial_features, block_definitions, layouts CASCADE;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 버전의 전체 수명 주기를 검증한다: 저장(동일하면 거부) → 작업본과 비교 → 새 버전 → 복원(필요 시 pre-restore 보존)
    /// → 분기 도면 생성 및 독립 편집 → 기준선 보호/이동 → 삭제와 부모 재연결 → 도면 삭제(마지막 도면 보호).
    /// 전제: SPATIAL_TEST_CONNECTION 이 빈 PostGIS DB 를 가리킴.
    /// </summary>
    [FactIfDatabase]
    public async Task VersionLifecycle_SaveCompareRestoreBranchBaselineDelete()
    {
        // 0) 빈 상태에서 시작하고 스키마(작업 테이블과 버전 테이블)를 만든다.
        await ResetAsync();
        var context = new ChangeContext(1, "tester", Guid.NewGuid());
        await using var repo = new PostGisFeatureRepository(Connection);
        await repo.EnsureSchemaAsync();
        await using var versions = new VersionRepository(Connection);
        await versions.EnsureSchemaAsync();

        // 1) 레이어 "Equipment" 에 설비 2개를 가져온다.
        await repo.ImportAsync(
            Array.Empty<(string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>, string)>(),
            new (string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>)[]
            {
                ("EQ", "Equipment", Square(0, 0), Square(0, 0), Attributes(0, 0)),
                ("EQ", "Equipment", Square(100, 0), Square(100, 0), Attributes(100, 0))
            }, "test.dxf", changeContext: context);

        // 2) 첫 저장은 버전 1(설비 2개)을 만들고, 변경 없이 다시 저장하면 거부(null)되며 상태는 "수정 없음"이다.
        var v1 = await versions.CreateVersionAsync(1, "첫 배치", "처음", VersionKind.Manual, context);
        Assert.NotNull(v1);
        Assert.Equal(1, v1!.VersionNo);
        Assert.Equal(2, v1.BlockCount);
        Assert.Null(await versions.CreateVersionAsync(1, "same", null, VersionKind.Manual, context));
        var status = await versions.GetStatusAsync(1);
        Assert.False(status!.IsModified);
        Assert.Equal(1, status.BaseVersionNo);

        // 3) 일반 저장 경로(ApplyLayerEditsAsync)로 설비 하나를 25만큼 이동한다(삽입 X 속성도 함께 갱신).
        var objects = await repo.FindEditableBlockInstancesAsync();
        var moved = objects.Single(o => o.Geometry.EnvelopeInternal.MinX == 100);
        var shifted = new NetTopologySuite.Geometries.Utilities.AffineTransformation().Translate(25, 0);
        var attributes = new Dictionary<string, string?>(moved.Attributes) { ["block_insert_x"] = "125" };
        await repo.ApplyLayerEditsAsync(
            new[] { (moved.Id, shifted.Transform(moved.Geometry), shifted.Transform(moved.OuterGeometry), moved.UpdatedAt, true, (IReadOnlyDictionary<string, string?>?)attributes) },
            Array.Empty<(EditableLayerObject, Geometry, Geometry)>(),
            Array.Empty<(long, DateTime, bool)>(), changeContext: context);

        Assert.True((await versions.GetStatusAsync(1))!.IsModified);

        // 4) 작업본과 v1 비교: 정확히 이동 1건이어야 한다(객체 UID 유지).
        var diff = await versions.DiffAsync(1, v1.Id, null);
        Assert.Equal(1, diff.Count(DiffKind.Moved));
        Assert.Single(diff.Entries);

        // 5) 두 번째 버전 저장: 번호 2, 부모는 v1, v1 과 v2 의 차이는 이동 1건.
        var v2 = await versions.CreateVersionAsync(1, "오른쪽으로", null, VersionKind.Manual, context);
        Assert.Equal(2, v2!.VersionNo);
        Assert.Equal(v1.Id, v2.ParentVersionId);
        Assert.Equal(1, (await versions.DiffAsync(1, v1.Id, v2.Id)).Count(DiffKind.Moved));

        // 6) v1 복원: 작업본이 v1 과 같아지고 UID 는 변하지 않는다.
        var uidsBefore = (await versions.GetWorkingBlocksAsync(1)).Select(b => b.ObjectUid).OrderBy(u => u).ToList();
        var safety = await versions.RestoreVersionAsync(1, v1.Id, context);
        Assert.Null(safety); // working copy already equalled v2 (the newest version): nothing extra to save
        Assert.True((await versions.DiffAsync(1, v1.Id, null)).IsEmpty);
        Assert.Equal(uidsBefore, (await versions.GetWorkingBlocksAsync(1)).Select(b => b.ObjectUid).OrderBy(u => u).ToList());
        Assert.False((await versions.GetStatusAsync(1))!.IsModified);

        // 7) 복원 후 다시 편집하고 v2 로 복원하면, 저장되지 않은 상태가 pre-restore 버전으로 보존된다.
        objects = await repo.FindEditableBlockInstancesAsync();
        var again = objects.First();
        await repo.ApplyLayerEditsAsync(
            new[] { (again.Id, shifted.Transform(again.Geometry), shifted.Transform(again.OuterGeometry), again.UpdatedAt, true,
                (IReadOnlyDictionary<string, string?>?)new Dictionary<string, string?>(again.Attributes) { ["block_insert_x"] = "25" }) },
            Array.Empty<(EditableLayerObject, Geometry, Geometry)>(),
            Array.Empty<(long, DateTime, bool)>(), changeContext: context);
        var pre = await versions.RestoreVersionAsync(1, v2.Id, context);
        Assert.NotNull(pre);
        Assert.Equal(VersionKind.PreRestore, pre!.Kind);
        Assert.True((await versions.DiffAsync(1, v2.Id, null)).IsEmpty);

        // 8) 분기: v1 의 복사본으로 새 도면을 만들고 독립적으로 편집한다(원래 도면에는 영향 없음).
        var branchId = await versions.CreateLayoutFromVersionAsync(v1.Id, "B안", null, context);
        Assert.NotEqual(1, branchId);
        await using var branchRepo = new PostGisFeatureRepository(Connection, branchId);
        var branchObjects = await branchRepo.FindEditableBlockInstancesAsync();
        Assert.Equal(2, branchObjects.Count);
        Assert.True((await versions.DiffAsync(branchId, v1.Id, null)).IsEmpty);
        await branchRepo.ApplyLayerEditsAsync(
            Array.Empty<(long, Geometry, Geometry, DateTime, bool, IReadOnlyDictionary<string, string?>?)>(),
            Array.Empty<(EditableLayerObject, Geometry, Geometry)>(),
            new[] { (branchObjects[0].Id, branchObjects[0].UpdatedAt, true) }, changeContext: context);
        Assert.Equal(2, (await repo.FindEditableBlockInstancesAsync()).Count);   // layout 1 untouched
        Assert.Single(await branchRepo.FindEditableBlockInstancesAsync());
        Assert.Equal(1, (await versions.DiffAsync(branchId, v1.Id, null)).Count(DiffKind.Removed));

        // 9) 기준선 보호: 기준선 버전은 삭제/이름 변경 불가, 다른 버전을 기준선으로 지정하면 이전 기준선은 released 로 바뀌고, 삭제하면 후속 버전은 부모가 재연결(여기서는 null)된다.
        await versions.SetStatusAsync(v1.Id, VersionStatus.Baseline);
        await Assert.ThrowsAsync<InvalidOperationException>(() => versions.DeleteVersionAsync(v1.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => versions.UpdateVersionAsync(v1.Id, "x", null));
        await versions.SetStatusAsync(v2.Id, VersionStatus.Baseline);
        Assert.Equal(VersionStatus.Released, (await versions.GetVersionAsync(v1.Id))!.Status);
        await versions.DeleteVersionAsync(v1.Id);
        Assert.Null(await versions.GetVersionAsync(v1.Id));
        Assert.Null((await versions.GetVersionAsync(v2.Id))!.ParentVersionId);

        // 10) 도면을 삭제하면 작업본과 버전도 함께 삭제되며 마지막 남은 도면은 삭제할 수 없다.
        Assert.True(await versions.DeleteLayoutAsync(branchId));
        Assert.False(await versions.DeleteLayoutAsync(1));
    }

    /// <summary>
    /// 버전 관리 도입 이전 스키마(layouts 와 layout_id/object_uid 열이 없음)로 되돌린 DB 를 EnsureSchemaAsync 가 제자리에서 업그레이드하고,
    /// 기존 객체마다 서로 다른 UID 를 부여하며 기본 도면이 하나 생기는지 검증한다.
    /// 전제: SPATIAL_TEST_CONNECTION 이 빈 PostGIS DB 를 가리킴.
    /// </summary>
    [FactIfDatabase]
    public async Task ExistingDatabaseWithoutLayoutsIsMigratedAndGetsUniqueUids()
    {
        // 1) 빈 DB 에서 스키마를 만들고 설비 2개를 가져온다.
        await ResetAsync();
        await using var repo = new PostGisFeatureRepository(Connection);
        await repo.EnsureSchemaAsync();
        await repo.ImportAsync(
            Array.Empty<(string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>, string)>(),
            new (string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>)[]
            {
                ("EQ", "Equipment", Square(0, 0), Square(0, 0), Attributes(0, 0)),
                ("EQ", "Equipment", Square(50, 0), Square(50, 0), Attributes(50, 0))
            }, "old.dxf");

        // 2) 버전 관리 이전 스키마로 되돌린다(layouts 테이블, layout_id/object_uid 열, 관련 인덱스 제거).
        await using (var connection = new Npgsql.NpgsqlConnection(Connection))
        {
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand("""
                DROP INDEX IF EXISTS uq_spatial_block_instances_uid, uq_spatial_features_uid;
                ALTER TABLE spatial_block_instances DROP COLUMN layout_id, DROP COLUMN object_uid;
                ALTER TABLE spatial_features DROP COLUMN layout_id, DROP COLUMN object_uid;
                ALTER TABLE change_log DROP COLUMN layout_id;
                DROP TABLE layouts CASCADE;
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        // 3) 앱 시작 시와 같이 스키마 단계를 다시 실행하면 제자리 업그레이드되어야 한다.
        await repo.EnsureSchemaAsync();
        await using var versions = new VersionRepository(Connection);
        await versions.EnsureSchemaAsync();

        // 4) 검증: 블록 2개, UID 가 서로 다름, 도면 1개, 편집 가능 객체 2개.
        var blocks = await versions.GetWorkingBlocksAsync(1);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(2, blocks.Select(b => b.ObjectUid).Distinct().Count());
        Assert.Single(await versions.ListLayoutsAsync());
        Assert.Equal(2, (await repo.FindEditableBlockInstancesAsync()).Count);
    }

    /// <summary>
    /// 한 도면에서의 레이어 이름 변경/삭제가 다른 도면에 영향을 주지 않고, 변경 로그도 도면별로 분리되어
    /// 다른 도면의 세션에는 알려지지 않는지 검증한다.
    /// 전제: SPATIAL_TEST_CONNECTION 이 빈 PostGIS DB 를 가리킴.
    /// </summary>
    [FactIfDatabase]
    public async Task EditsAndLayerOperationsStayInsideTheirLayout()
    {
        // 1) 빈 DB 에서 설비 1개를 가져와 버전을 만들고 그 버전으로 두 번째 도면("Other")을 만든다.
        await ResetAsync();
        var context = new ChangeContext(1, "tester", Guid.NewGuid());
        await using var repo = new PostGisFeatureRepository(Connection);
        await repo.EnsureSchemaAsync();
        await using var versions = new VersionRepository(Connection);
        await versions.EnsureSchemaAsync();
        await repo.ImportAsync(
            Array.Empty<(string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>, string)>(),
            new (string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>)[] { ("EQ", "Equipment", Square(0, 0), Square(0, 0), Attributes(0, 0)) },
            "t.dxf", changeContext: context);
        var v = await versions.CreateVersionAsync(1, "v", null, VersionKind.Manual, context);
        var other = await versions.CreateLayoutFromVersionAsync(v!.Id, "Other", null, context);

        await using var otherRepo = new PostGisFeatureRepository(Connection, other);
        Assert.Equal("Equipment", (await otherRepo.FindImportedLayersAsync()).Single().LayerName);

        // 2) 한쪽 도면에서의 이름 변경/삭제가 다른 도면에는 영향을 주지 않는다.
        Assert.True(await otherRepo.LayerExistsAsync("Equipment"));
        await otherRepo.RenameLayerAsync("Equipment", "Renamed", changeContext: context);
        Assert.Equal("Renamed", (await otherRepo.FindImportedLayersAsync()).Single().LayerName);
        Assert.Equal("Equipment", (await repo.FindImportedLayersAsync()).Single().LayerName);
        await repo.DeleteLayerAsync("Equipment", changeContext: context);
        Assert.Empty(await repo.FindImportedLayersAsync());
        Assert.Single(await otherRepo.FindImportedLayersAsync());

        // 3) 변경은 도면별로 기록되므로, 다른 도면의 세션은 "Renamed" 변경을 통지받지 않는다.
        await using var feed = new ChangeFeedRepository(Connection);
        var changes = await feed.GetChangesSinceAsync(0, Guid.NewGuid(), null, layoutId: other);
        Assert.Contains(changes.Summaries, summary => summary.LayerName == "Renamed");
        var firstLayout = await feed.GetChangesSinceAsync(0, Guid.NewGuid(), null, layoutId: 1);
        Assert.DoesNotContain(firstLayout.Summaries, summary => summary.LayerName == "Renamed");
    }
}
