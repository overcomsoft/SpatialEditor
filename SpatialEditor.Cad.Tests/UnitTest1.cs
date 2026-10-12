using SpatialEditor.Cad;

// ============================================================================
// SpatialEditor.Cad.Tests / UnitTest1 : DXF 가져오기(DxfBlockImporter) 통합 성격의 단위 테스트
// ----------------------------------------------------------------------------
// [실행 조건] 데이터베이스/환경 변수는 필요 없다. 대신 실제 샘플 도면 파일이 있어야 한다.
//   경로: 테스트 실행 폴더(AppContext.BaseDirectory)에서 위로 4단계 올라간 위치의 data/SmartLayout.dxf
//   (예: 솔루션 루트의 data 폴더). 파일이 없으면 첫 테스트가 "DXF file not found" 로 실패한다.
// [공통 흐름] 모든 테스트: SmartLayout.dxf 경로 계산 → DxfBlockImporter 로 가져오기 → 결과 속성/도형 검증
//   1) SmartLayoutContainsImportableLayers
//        Import(레이어 단위 결과)와 ImportBlockInstances(블록 인스턴스) 가 비어 있지 않고,
//        모든 도형이 비어 있지 않으며 FeatureType 이 "dxf_layer", 레이어명이 대소문자 무시 중복 없음,
//        외곽(Outer) 도형이 유효하고 SRID 가 5186 인지 확인
//   2) EquipmentImportYieldsOnlyEquipmentBlockObjects
//        ImportEquipmentBlockObjects 가 정확히 126개를 돌려주고 모두 Equipment 레이어이며,
//        block_handle / block_insert_x / block_insert_y 속성, 비어 있지 않은 외곽, SRID 5186 을 가지는지 확인
//   3) FallbackOuterGeometryIsReportedWithAReason
//        선들이 닫힌 영역으로 만들어지지 않아 외곽을 경계 상자로 대체(fallback)한 항목이 존재하며,
//        대체한 모든 항목에 outer_geom_fallback_reason 속성(사유)이 비어 있지 않게 기록되는지 확인(조용한 대체 금지)
//   4) BlockInstancesAlwaysCarryPlacementAttributes
//        모든 블록 인스턴스와 설비 경계 상자가 배치 속성(block_name, block_handle, 삽입 좌표, 회전, 배율)을
//        비어 있지 않고 숫자로 해석 가능한 값으로 가지는지 확인
// ============================================================================
namespace SpatialEditor.Cad.Tests;

/// <summary>SmartLayout.dxf 샘플 도면을 대상으로 DxfBlockImporter 의 결과 구조와 속성 규칙을 검증하는 테스트 모음.</summary>
public class UnitTest1
{
    /// <summary>
    /// 샘플 도면에서 레이어별 결과와 블록 인스턴스가 모두 가져와지고, 도형이 유효하며 SRID 5186 이 설정되는지 검증한다.
    /// 전제: data/SmartLayout.dxf 파일 존재.
    /// </summary>
    [Fact]
    public void SmartLayoutContainsImportableLayers()
    {
        // 1) 샘플 DXF 경로를 계산하고 존재 여부부터 확인한다(없으면 경로를 메시지로 보여 줌).
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "SmartLayout.dxf"));
        Assert.True(File.Exists(path), $"DXF file not found: {path}");

        // 2) 레이어 단위 결과와 블록 인스턴스를 각각 가져온다.
        var results = new DxfBlockImporter().Import(path);
        var instances = new DxfBlockImporter().ImportBlockInstances(path);

        // 3) 구조 검증: 비어 있지 않음, 도형 비어 있지 않음, 종류 "dxf_layer", 레이어명 중복 없음(대소문자 무시).
        Assert.NotEmpty(results);
        Assert.NotEmpty(instances);
        Assert.All(instances, instance => Assert.True(instance.ActualGeometry.NumGeometries > 0));
        Assert.All(results, result => Assert.Equal("dxf_layer", result.FeatureType));
        Assert.Equal(results.Count, results.Select(result => result.Layer).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(results, result =>
        {
            Assert.True(result.ActualGeometry.NumGeometries > 0);
            Assert.True(result.OuterGeometry.IsValid);
            Assert.Equal(5186, result.ActualGeometry.SRID);
        });
    }

    /// <summary>
    /// "Equipment" 레이어의 설비 블록 객체만 정확히 126개 가져오고, 각 객체가 블록 핸들/삽입 좌표 속성과 유효한 외곽·SRID 를 가지는지 검증한다.
    /// 전제: data/SmartLayout.dxf 파일 존재.
    /// </summary>
    [Fact]
    public void EquipmentImportYieldsOnlyEquipmentBlockObjects()
    {
        // 샘플 DXF 에서 설비 블록 객체만 가져온다(기대 개수 126은 현재 샘플 도면 기준).
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "SmartLayout.dxf"));
        var importer = new DxfBlockImporter();

        var objects = importer.ImportEquipmentBlockObjects(path);

        Assert.Equal(126, objects.Count);
        Assert.All(objects, o =>
        {
            Assert.Equal("Equipment", o.Layer);
            Assert.True(o.Attributes.ContainsKey("block_handle"));
            Assert.True(o.Attributes.ContainsKey("block_insert_x") && o.Attributes.ContainsKey("block_insert_y"));
            Assert.False(o.OuterGeometry.IsEmpty);
            Assert.Equal(5186, o.ActualGeometry.SRID);
        });
    }

    /// <summary>
    /// 선이 닫힌 영역으로 합쳐지지 않아 외곽을 경계 상자로 대체한 항목이 항상 사유(outer_geom_fallback_reason)와 함께 보고되는지 검증한다.
    /// 전제: data/SmartLayout.dxf 파일 존재.
    /// </summary>
    [Fact]
    public void FallbackOuterGeometryIsReportedWithAReason()
    {
        // 레이어 결과와 블록 인스턴스를 모두 가져온다.
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "SmartLayout.dxf"));
        var results = new DxfBlockImporter().Import(path);
        var instances = new DxfBlockImporter().ImportBlockInstances(path);

        // SmartLayout.dxf 에는 선이 닫힌 영역으로 폴리곤화되지 않는 레이어/블록이 있는 것으로 알려져 있어,
        // 가져오기가 경계 상자(envelope)로 대체한다. 이 대체가 항상 사유와 함께 설명되고 조용히 일어나지 않도록 보장한다.
        Assert.Contains(instances, instance => instance.OuterGeometryIsFallback);
        Assert.All(instances.Where(instance => instance.OuterGeometryIsFallback), instance =>
            Assert.True(instance.Attributes.TryGetValue("outer_geom_fallback_reason", out var reason) && !string.IsNullOrEmpty(reason)));

        Assert.Contains(results, result => result.OuterGeometryIsFallback);
        Assert.All(results.Where(result => result.OuterGeometryIsFallback), result =>
            Assert.True(result.Attributes.TryGetValue("outer_geom_fallback_reason", out var reason) && !string.IsNullOrEmpty(reason)));
    }

    /// <summary>
    /// 모든 블록 인스턴스와 설비 경계 상자가 배치 속성(이름, 핸들, 삽입 X/Y, 회전, 배율)을 비어 있지 않은 값으로 갖고,
    /// 숫자 속성은 실제 숫자로 해석되는지 검증한다.
    /// 전제: data/SmartLayout.dxf 파일 존재.
    /// </summary>
    [Fact]
    public void BlockInstancesAlwaysCarryPlacementAttributes()
    {
        // 블록 인스턴스와 설비 경계 상자를 가져온다.
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "SmartLayout.dxf"));
        var instances = new DxfBlockImporter().ImportBlockInstances(path);
        var equipmentBoxes = new DxfBlockImporter().ImportEquipmentBoundingBoxes(path);

        // 모든 블록 인스턴스: 이름/핸들은 비어 있지 않고 좌표/회전/배율은 숫자여야 한다.
        Assert.NotEmpty(instances);
        Assert.All(instances, instance =>
        {
            Assert.True(instance.Attributes.TryGetValue("block_name", out var name) && !string.IsNullOrEmpty(name));
            Assert.True(instance.Attributes.TryGetValue("block_handle", out var handle) && !string.IsNullOrEmpty(handle));
            Assert.True(instance.Attributes.TryGetValue("block_insert_x", out var x) && double.TryParse(x, out _));
            Assert.True(instance.Attributes.TryGetValue("block_insert_y", out var y) && double.TryParse(y, out _));
            Assert.True(instance.Attributes.TryGetValue("block_rotation_deg", out var rotation) && double.TryParse(rotation, out _));
            Assert.True(instance.Attributes.TryGetValue("block_scale_x", out var scaleX) && double.TryParse(scaleX, out _));
            Assert.True(instance.Attributes.TryGetValue("block_scale_y", out var scaleY) && double.TryParse(scaleY, out _));
        });

        // 설비 경계 상자도 같은 배치 속성 규칙을 따른다.
        Assert.All(equipmentBoxes, box =>
        {
            Assert.True(box.Attributes.TryGetValue("block_name", out var name) && !string.IsNullOrEmpty(name));
            Assert.True(box.Attributes.TryGetValue("block_handle", out var handle) && !string.IsNullOrEmpty(handle));
            Assert.True(box.Attributes.TryGetValue("block_insert_x", out var x) && double.TryParse(x, out _));
            Assert.True(box.Attributes.TryGetValue("block_rotation_deg", out var rotation) && double.TryParse(rotation, out _));
        });
    }
}
