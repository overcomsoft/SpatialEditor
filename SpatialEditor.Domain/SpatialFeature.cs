using NetTopologySuite.Geometries;

// ============================================================================
// SpatialFeature.cs : 화면 표시/편집에 쓰이는 공간 객체 읽기 모델
//
//   SpatialFeature       : 지도에 그릴 일반 도형 1개(지오메트리 + 속성)
//   LayerInfo            : 레이어 목록(레이어 패널)용 요약
//   EditableLayerObject  : 편집 모드에서 선택/이동/수정 대상이 되는 객체 1개
//
//   저장소(Repository)가 DB(PostGIS) 행을 읽어 이 레코드로 변환하고, UI 가 그대로 사용합니다.
// ============================================================================
namespace SpatialEditor.Domain;

/// <summary>지도에 표시하는 공간 객체 하나(도형 + 속성)입니다.</summary>
/// <param name="Id">객체 DB id.</param>
/// <param name="FeatureType">객체 종류 문자열(실제 값: "dxf_layer", "cad_block", "block_instance").</param>
/// <param name="Geometry">NetTopologySuite 지오메트리(도면 좌표계).</param>
/// <param name="Attributes">속성 키-값 사전. 값이 없으면 null.</param>
/// <param name="LayerName">소속 레이어 이름. 알 수 없으면 null(기본값).</param>
public sealed record SpatialFeature(
    long Id,
    string FeatureType,
    Geometry Geometry,
    IReadOnlyDictionary<string, string?> Attributes,
    string? LayerName = null);

/// <summary>레이어 패널에 표시할 레이어 요약 정보입니다.</summary>
/// <param name="LayerName">레이어 이름.</param>
/// <param name="GeometryTypes">레이어에 포함된 지오메트리 종류를 합친 표시용 문자열.</param>
/// <param name="EntityCount">레이어의 일반 도형(엔티티) 수.</param>
/// <param name="InstanceCount">레이어의 블록 인스턴스 수(기본값 0).</param>
public sealed record LayerInfo(
    string LayerName,
    string GeometryTypes,
    long EntityCount,
    long InstanceCount = 0);

/// <summary>편집 모드에서 다루는 객체(블록 인스턴스 또는 일반 도형) 하나입니다.</summary>
/// <param name="Id">객체 DB id.</param>
/// <param name="FeatureType">객체 종류 문자열.</param>
/// <param name="BlockName">블록 객체이면 블록 이름, 아니면 null.</param>
/// <param name="SourceFile">가져온 원본 DXF 파일 이름. 없으면 null.</param>
/// <param name="LayerName">소속 레이어 이름. 없으면 null.</param>
/// <param name="Geometry">객체의 실제 지오메트리.</param>
/// <param name="OuterGeometry">객체의 외곽(폴리곤화한) 지오메트리. 선택/충돌 판정과 외곽 표시에 사용.</param>
/// <param name="Attributes">속성 키-값 사전. 값이 없으면 null.</param>
/// <param name="UpdatedAt">마지막 수정 시각.</param>
public sealed record EditableLayerObject(
    long Id,
    string FeatureType,
    string? BlockName,
    string? SourceFile,
    string? LayerName,
    Geometry Geometry,
    Geometry OuterGeometry,
    IReadOnlyDictionary<string, string?> Attributes,
    DateTime UpdatedAt);