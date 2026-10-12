using NetTopologySuite.Geometries;

// ============================================================================
// CadBlockInstanceResult : DXF 의 INSERT(블록 참조) 1개를 읽은 결과 레코드
// ----------------------------------------------------------------------------
// 흐름도
//   DxfBlockImporter.ImportBlockInstances / ImportEquipmentBlockObjects
//     → INSERT 마다 블록 정의 내부 엔티티를 변환행렬로 월드 좌표화
//     → 실제 선형(ActualGeometry) + 폴리곤화한 외곽(OuterGeometry) + 속성(Attributes)
//     → 이 레코드로 묶어 호출자(MainWindow 의 블록 객체 편집/저장)에 전달
// ============================================================================
namespace SpatialEditor.Cad;

/// <summary>
/// DXF 블록 인스턴스(INSERT) 1개를 가져온 결과. 불변(record) 값 객체입니다.
/// </summary>
/// <param name="BlockName">블록 정의 이름(같은 종류의 인스턴스가 공유). null 아님.</param>
/// <param name="Layer">INSERT 가 놓인 DXF 레이어 이름. 레이어가 없으면 null 가능.</param>
/// <param name="ActualGeometry">블록 내부의 실제 선/폴리곤을 월드 좌표로 변환해 모은 GeometryCollection(원본 선화 형태).</param>
/// <param name="OuterGeometry">선화를 Polygonize 해 만든 외곽 면(선택/이동용). 실패 시 외곽 사각형(Envelope)으로 대체됨.</param>
/// <param name="Attributes">ATTRIB 태그/값과 삽입점·회전·스케일 등 배치 정보(키는 대소문자 무시). 값은 null 가능.</param>
/// <param name="OuterGeometryIsFallback">true 면 OuterGeometry 가 폴리곤화 실패로 Envelope 사각형으로 대체된 것. 기본 false.</param>
public sealed record CadBlockInstanceResult(
    string BlockName,
    string? Layer,
    Geometry ActualGeometry,
    Geometry OuterGeometry,
    IReadOnlyDictionary<string, string?> Attributes,
    bool OuterGeometryIsFallback = false);