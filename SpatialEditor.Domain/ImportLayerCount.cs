// ============================================================================
// ImportLayerCount.cs : DXF 가져오기 결과의 레이어별 집계 레코드
//   가져오기 서비스가 레이어별로 저장된 객체 수를 세어 UI/감사 로그에 보여줄 때 사용합니다.
// ============================================================================
namespace SpatialEditor.Domain;

/// <summary>레이어 하나에 대해 가져온 객체 수 집계입니다.</summary>
/// <param name="LayerName">DXF 레이어 이름.</param>
/// <param name="EntityCount">해당 레이어에 저장된 일반 도형(엔티티) 수.</param>
/// <param name="BlockInstanceCount">해당 레이어에 저장된 블록 인스턴스 수.</param>
public sealed record ImportLayerCount(
    string LayerName,
    long EntityCount,
    long BlockInstanceCount);