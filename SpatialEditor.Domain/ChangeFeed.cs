// ============================================================================
// ChangeFeed.cs : 다중 사용자 동기화용 "변경 이력(change feed)" 도메인 모델
//
//   저장 시:  편집기 -> ChangeContext(누가/어느 세션) + ChangeEntry 목록(무엇을/어떻게)
//                    -> 저장소가 변경 로그 테이블에 한 건씩 기록(시퀀스 번호 부여)
//   조회 시:  다른 세션이 마지막으로 본 시퀀스 번호 이후의 로그를 요청
//                    -> 레이어/사용자별로 묶은 ChangeSummary 목록 + 새 기준 번호를 ChangeBatch 로 반환
//                    -> UI 가 "OOO 님이 레이어 X 를 수정했습니다" 알림을 표시
//
//   ChangeOperation / ChangeEntityType 은 로그에 저장되는 문자열/문자 코드 상수입니다.
// ============================================================================
namespace SpatialEditor.Domain;

/// <summary>
/// 저장을 수행하는 주체 정보입니다. 모든 변경과 함께 기록되어 다른 세션이 "누가 무엇을 바꿨는지" 보여줄 수 있습니다.
/// </summary>
/// <param name="UserId">저장한 사용자의 DB id. 로그인하지 않았거나 알 수 없으면 null.</param>
/// <param name="UserName">저장한 사용자의 표시 이름. 알림 메시지에 쓰이며 없으면 null.</param>
/// <param name="SessionId">저장한 프로그램 인스턴스(세션)의 고유 id. 자기 자신이 만든 변경을 알림에서 제외할 때 사용합니다.</param>
public sealed record ChangeContext(long? UserId, string? UserName, Guid SessionId);

/// <summary>
/// 저장된 변경 한 건입니다. EntityId 가 null 이면 레이어 전체 가져오기/삭제를 뜻합니다.
/// </summary>
/// <param name="LayerName">변경이 일어난 레이어 이름.</param>
/// <param name="EntityType">변경 대상 종류. <see cref="ChangeEntityType"/> 상수 중 하나.</param>
/// <param name="EntityId">변경된 객체의 id. 레이어 전체 변경이면 null.</param>
/// <param name="Operation">변경 종류 문자. <see cref="ChangeOperation"/> 상수('I'/'U'/'D') 중 하나.</param>
public sealed record ChangeEntry(string LayerName, string EntityType, long? EntityId, char Operation);

/// <summary>
/// 한 사용자가 한 레이어에서 수행한 변경을 건수로 요약한 것입니다("데이터가 변경되었습니다" 알림용).
/// </summary>
/// <param name="LayerName">변경된 레이어 이름.</param>
/// <param name="UserName">변경한 사용자 표시 이름. 알 수 없으면 null.</param>
/// <param name="Added">추가된 객체 수.</param>
/// <param name="Updated">수정된 객체 수.</param>
/// <param name="Deleted">삭제된 객체 수.</param>
/// <param name="WholeLayer">true 이면 개별 객체가 아니라 레이어 전체 가져오기/삭제로 인한 변경.</param>
public sealed record ChangeSummary(string LayerName, string? UserName, int Added, int Updated, int Deleted, bool WholeLayer);

/// <summary>
/// 특정 시퀀스 번호 이후에 발생한 모든 변경의 묶음입니다. LatestSeq 가 다음 조회의 새 기준점이 됩니다.
/// </summary>
/// <param name="LatestSeq">이번 조회 시점의 가장 최신 시퀀스 번호. 호출자는 이 값을 저장해 다음에 "이후 변경"을 물어봅니다.</param>
/// <param name="Summaries">레이어/사용자별 변경 요약 목록. 변경이 없으면 빈 목록.</param>
public sealed record ChangeBatch(long LatestSeq, IReadOnlyList<ChangeSummary> Summaries)
{
    /// <summary>요약이 하나라도 있으면 true. UI 가 알림을 띄울지 판단하는 데 씁니다.</summary>
    public bool HasChanges => Summaries.Count > 0;
}

/// <summary>변경 로그에 저장되는 작업 종류 문자 코드 모음입니다(1글자 char).</summary>
public static class ChangeOperation
{
    /// <summary>객체 추가(Insert).</summary>
    public const char Insert = 'I';
    /// <summary>객체 수정(Update).</summary>
    public const char Update = 'U';
    /// <summary>객체 삭제(Delete).</summary>
    public const char Delete = 'D';
}

/// <summary>변경 로그에 저장되는 대상 종류 문자열 상수 모음입니다.</summary>
public static class ChangeEntityType
{
    /// <summary>설비 블록 인스턴스(배치된 블록 객체).</summary>
    public const string BlockInstance = "block_instance";
    /// <summary>일반(블록이 아닌) 도형 객체.</summary>
    public const string Feature = "feature";
    /// <summary>DXF 가져오기/레이어 삭제처럼 레이어 전체에 걸친 작업(개별 객체 id 없음).</summary>
    public const string Import = "import";
}
