// ============================================================================
// Versions.cs : 도면(레이아웃) 버전 관리 도메인 모델
//
//   LayoutInfo      : 도면 1건(작업본 + 저장된 버전들의 소유자)
//   LayoutVersion   : 저장된 스냅샷 1개(번호, 상태, 종류, 내용 해시)
//   VersionBlockItem / VersionFeatureItem : 버전(또는 작업본)에 담긴 블록 / 일반 객체
//   VersionDiffer(별도 파일)가 두 상태의 Block/FeatureItem 을 object_uid 로 비교해
//   DiffEntry(DiffKind) 목록을 담은 VersionDiff 를 만듭니다.
//   LayoutStatus    : 작업본이 시작 버전 대비 수정되었는지 표시
//
//   흐름:  편집 저장 -> 자동 버전(Auto) / 수동 저장(Manual) / 복원 직전 백업(PreRestore)
//         -> 버전 비교 화면은 VersionDiff 로 추가/삭제/이동/회전/형상/속성 변경을 보여줌
// ============================================================================
using NetTopologySuite.Geometries;

namespace SpatialEditor.Domain;

/// <summary>작업본과 저장된 버전들을 함께 관리하는 도면(레이아웃) 한 건입니다.</summary>
/// <param name="Id">도면 DB id.</param>
/// <param name="Name">도면 이름.</param>
/// <param name="Description">설명. 없으면 null.</param>
/// <param name="BaseVersionId">작업본이 시작된 기준 버전 id. 아직 없으면 null.</param>
/// <param name="CreatedAt">생성 시각.</param>
/// <param name="CreatedByName">생성한 사용자 이름. 알 수 없으면 null.</param>
/// <param name="ObjectCount">작업본에 들어 있는 객체 수.</param>
public sealed record LayoutInfo(
    long Id,
    string Name,
    string? Description,
    long? BaseVersionId,
    DateTime CreatedAt,
    string? CreatedByName,
    int ObjectCount);

/// <summary>버전 상태 문자열 상수입니다(DB 에 그대로 저장됨).</summary>
public static class VersionStatus
{
    /// <summary>초안: 아직 확정되지 않은 버전.</summary>
    public const string Draft = "draft";
    /// <summary>배포됨: 공식 확정된 버전.</summary>
    public const string Released = "released";
    /// <summary>기준선: 비교/복원의 기준으로 삼는 버전.</summary>
    public const string Baseline = "baseline";
    /// <summary>보관됨: 더 이상 사용하지 않고 이력으로만 남긴 버전.</summary>
    public const string Archived = "archived";
}

/// <summary>버전이 만들어진 경위(종류) 문자열 상수입니다.</summary>
public static class VersionKind
{
    /// <summary>사용자가 의도적으로 저장한 버전.</summary>
    public const string Manual = "manual";
    /// <summary>편집 저장 후 자동으로 저장된 버전.</summary>
    public const string Auto = "auto";
    /// <summary>복원이 작업본을 덮어쓰기 직전에 자동으로 백업해 둔 버전(실수 복구용).</summary>
    public const string PreRestore = "pre-restore";
}

/// <summary>도면의 저장된 스냅샷 하나입니다.</summary>
/// <param name="Id">버전 DB id.</param>
/// <param name="LayoutId">소속 도면 id.</param>
/// <param name="VersionNo">도면 안에서의 버전 번호(1부터 증가).</param>
/// <param name="Label">버전 라벨(사용자에게 보이는 이름).</param>
/// <param name="Comment">저장 시 남긴 설명. 없으면 null.</param>
/// <param name="ParentVersionId">이 버전의 바탕이 된 부모 버전 id. 최초 버전이면 null.</param>
/// <param name="Status">상태. <see cref="VersionStatus"/> 상수 중 하나.</param>
/// <param name="Kind">생성 경위. <see cref="VersionKind"/> 상수 중 하나.</param>
/// <param name="CreatedBy">저장한 사용자 id. 알 수 없으면 null.</param>
/// <param name="CreatedByName">저장한 사용자 이름. 알 수 없으면 null.</param>
/// <param name="CreatedAt">저장 시각.</param>
/// <param name="BlockCount">버전에 포함된 블록 객체 수.</param>
/// <param name="FeatureCount">버전에 포함된 일반 객체 수.</param>
/// <param name="ContentHash">버전 내용으로 계산한 해시. 동일 내용 중복 저장 감지에 사용.</param>
public sealed record LayoutVersion(
    long Id,
    long LayoutId,
    int VersionNo,
    string Label,
    string? Comment,
    long? ParentVersionId,
    string Status,
    string Kind,
    long? CreatedBy,
    string? CreatedByName,
    DateTime CreatedAt,
    int BlockCount,
    int FeatureCount,
    string ContentHash);

/// <summary>버전(또는 작업본)에 저장된 설비 블록 인스턴스 하나입니다.</summary>
/// <param name="ObjectUid">객체의 영구 고유 id. 이동/회전해도 변하지 않아 버전 간 같은 객체를 식별하는 열쇠.</param>
/// <param name="BlockName">블록 이름.</param>
/// <param name="LayerName">소속 레이어. 없으면 null.</param>
/// <param name="SourceFile">원본 DXF 파일 이름. 없으면 null.</param>
/// <param name="InsertX">삽입점 X(도면 단위). 알 수 없으면 null.</param>
/// <param name="InsertY">삽입점 Y(도면 단위). 알 수 없으면 null.</param>
/// <param name="RotationDeg">회전각(도, 반시계 기준).</param>
/// <param name="ScaleX">X 축 배율(1 이 원본 크기).</param>
/// <param name="ScaleY">Y 축 배율(1 이 원본 크기).</param>
/// <param name="Attributes">속성 키-값 사전. 값이 없으면 null.</param>
/// <param name="Geometry">배치된 상태의 실제 지오메트리.</param>
/// <param name="OuterGeometry">배치된 상태의 외곽 지오메트리.</param>
public sealed record VersionBlockItem(
    Guid ObjectUid,
    string BlockName,
    string? LayerName,
    string? SourceFile,
    double? InsertX,
    double? InsertY,
    double RotationDeg,
    double ScaleX,
    double ScaleY,
    IReadOnlyDictionary<string, string?> Attributes,
    Geometry Geometry,
    Geometry OuterGeometry);

/// <summary>버전(또는 작업본)에 저장된 일반(블록이 아닌) 객체 하나입니다.</summary>
/// <param name="ObjectUid">객체의 영구 고유 id(버전 간 같은 객체 식별용).</param>
/// <param name="FeatureType">객체 종류 문자열.</param>
/// <param name="BlockName">블록에서 분해되어 나온 객체이면 블록 이름, 아니면 null.</param>
/// <param name="LayerName">소속 레이어. 없으면 null.</param>
/// <param name="SourceFile">원본 DXF 파일 이름. 없으면 null.</param>
/// <param name="Attributes">속성 키-값 사전. 값이 없으면 null.</param>
/// <param name="Geometry">객체 지오메트리.</param>
/// <param name="OuterGeometry">외곽 지오메트리. 없으면 null.</param>
public sealed record VersionFeatureItem(
    Guid ObjectUid,
    string FeatureType,
    string? BlockName,
    string? LayerName,
    string? SourceFile,
    IReadOnlyDictionary<string, string?> Attributes,
    Geometry Geometry,
    Geometry? OuterGeometry);

/// <summary>두 상태 사이 차이의 종류입니다.</summary>
public enum DiffKind
{
    /// <summary>이전에는 없고 이후에 새로 생긴 객체.</summary>
    Added,
    /// <summary>이전에는 있었으나 이후에 사라진 객체.</summary>
    Removed,
    /// <summary>삽입 위치가 허용 오차보다 크게 이동한 블록.</summary>
    Moved,
    /// <summary>회전각이 허용 오차보다 크게 바뀐 블록.</summary>
    Rotated,
    /// <summary>블록 종류/배율이 바뀌었거나 일반 객체의 도형이 바뀜.</summary>
    Reshaped,
    /// <summary>레이어 또는 속성 값이 바뀜(위치 관련 속성은 제외).</summary>
    AttributesChanged
}

/// <summary>두 상태(버전 대 버전, 또는 버전 대 작업본) 사이의 차이 한 건입니다.</summary>
/// <param name="Kind">차이 종류.</param>
/// <param name="IsBlock">true 이면 블록 인스턴스, false 이면 일반 객체.</param>
/// <param name="ObjectUid">대상 객체의 영구 고유 id.</param>
/// <param name="Name">표시용 이름(블록 이름 또는 객체 종류).</param>
/// <param name="LayerName">대상 레이어. 없으면 null.</param>
/// <param name="Before">변경 전 상태를 설명하는 문자열. 추가된 객체이면 null.</param>
/// <param name="After">변경 후 상태를 설명하는 문자열. 삭제된 객체이면 null.</param>
/// <param name="BeforeGeometry">변경 전 지오메트리(화면 강조 표시용). 없으면 null.</param>
/// <param name="AfterGeometry">변경 후 지오메트리(화면 강조 표시용). 없으면 null.</param>
public sealed record DiffEntry(
    DiffKind Kind,
    bool IsBlock,
    Guid ObjectUid,
    string Name,
    string? LayerName,
    string? Before,
    string? After,
    Geometry? BeforeGeometry,
    Geometry? AfterGeometry);

/// <summary>두 상태를 비교한 차이 전체 결과입니다.</summary>
/// <param name="Entries">차이 항목 목록. 차이가 없으면 빈 목록.</param>
public sealed record VersionDiff(IReadOnlyList<DiffEntry> Entries)
{
    /// <summary>특정 종류의 차이 건수를 셉니다.</summary>
    /// <param name="kind">셀 차이 종류.</param>
    /// <returns>해당 종류의 항목 수.</returns>
    public int Count(DiffKind kind) => Entries.Count(entry => entry.Kind == kind);
    /// <summary>차이가 하나도 없으면 true.</summary>
    public bool IsEmpty => Entries.Count == 0;
}

/// <summary>작업본이 시작된 기준 버전 대비 현재 어떤 상태인지 나타냅니다.</summary>
/// <param name="LayoutId">도면 id.</param>
/// <param name="LayoutName">도면 이름.</param>
/// <param name="BaseVersionNo">기준 버전 번호. 기준 버전이 없으면 null.</param>
/// <param name="BaseVersionLabel">기준 버전 라벨. 기준 버전이 없으면 null.</param>
/// <param name="IsModified">작업본이 기준 버전과 달라졌으면 true.</param>
public sealed record LayoutStatus(long LayoutId, string LayoutName, int? BaseVersionNo, string? BaseVersionLabel, bool IsModified);
