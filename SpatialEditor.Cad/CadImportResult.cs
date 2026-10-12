using NetTopologySuite.Geometries;

// ============================================================================
// CadImportResult : DXF 레이어 단위(또는 장비 외곽 박스) 가져오기 결과 레코드
// ----------------------------------------------------------------------------
// 흐름도
//   DxfBlockImporter.Import(filePath)
//     → 엔티티 전부 순회, INSERT 는 블록 내부로 재귀 분해(변환행렬 누적)
//     → 레이어별 LayerAccumulator 에 지오메트리 집계
//     → 레이어마다 이 레코드 1개로 변환 (FeatureType = "dxf_layer")
//   DxfBlockImporter.ImportEquipmentBoundingBoxes(filePath)
//     → Equipment 레이어 INSERT 의 Envelope 사각형 → 이 레코드 (FeatureType = "cad_block")
// ============================================================================
namespace SpatialEditor.Cad;

/// <summary>
/// DXF 가져오기 결과 1건(레이어 집계 또는 장비 외곽 박스). 불변(record) 값 객체입니다.
/// </summary>
/// <param name="BlockName">객체 이름. 레이어 집계에서는 레이어 이름, 장비 박스에서는 블록 이름.</param>
/// <param name="Layer">저장될 레이어 이름. null 가능.</param>
/// <param name="ActualGeometry">원본 형태 그대로의 지오메트리(GeometryCollection 등, 월드 좌표).</param>
/// <param name="OuterGeometry">외곽 면. 닫힌 폴리곤이 없으면 Envelope 사각형으로 대체(OuterGeometryIsFallback=true).</param>
/// <param name="Attributes">텍스트 값, 지원되지 않는 엔티티 목록, 배치 정보 등 부가 속성. 값은 null 가능.</param>
/// <param name="FeatureType">피처 종류 문자열. 기본 "dxf_layer", 장비 박스는 "cad_block".</param>
/// <param name="EntityCount">이 결과에 포함된 엔티티 수. 기본 0.</param>
/// <param name="OuterGeometryIsFallback">외곽이 Envelope 대체본인지 여부. 기본 false.</param>
public sealed record CadImportResult(
    string BlockName,
    string? Layer,
    Geometry ActualGeometry,
    Geometry OuterGeometry,
    IReadOnlyDictionary<string, string?> Attributes,
    string FeatureType = "dxf_layer",
    int EntityCount = 0,
    bool OuterGeometryIsFallback = false);