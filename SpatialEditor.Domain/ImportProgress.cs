// ============================================================================
// ImportProgress.cs : DXF 가져오기 진행률 보고용 레코드
//   가져오기 작업이 단계가 바뀌거나 진행될 때마다 IProgress 로 UI 에 전달하여 진행 표시줄을 갱신합니다.
// ============================================================================
namespace SpatialEditor.Domain;

/// <summary>가져오기 진행 상태 한 시점입니다.</summary>
/// <param name="Stage">현재 단계 이름(화면에 표시되는 문구).</param>
/// <param name="Completed">현재 단계에서 완료한 항목 수.</param>
/// <param name="Total">현재 단계의 전체 항목 수. 알 수 없으면 0일 수 있습니다.</param>
public sealed record ImportProgress(string Stage, int Completed, int Total);
