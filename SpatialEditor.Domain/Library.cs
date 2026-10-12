using NetTopologySuite.Geometries;

// ============================================================================
// Library.cs : 블록 라이브러리(설비 카탈로그) 도메인 모델
//
//   BlockCategory (분류 트리, 부모-자식)  <-  LibraryItem.CategoryId
//   LibraryItem   (전역 라이브러리의 설비 블록 1종, 현재 버전의 로컬 지오메트리 포함)
//   LibraryItemEdit (카탈로그 필드만 수정할 때 쓰는 입력 모델)
//   LibraryRegisterOutcome / LibraryRegisterResult (도면의 블록을 라이브러리에 등록한 결과)
//   LibraryStatus (draft -> released -> deprecated 상태 문자열 상수)
//
//   데이터 흐름:  LibraryRepository(DB) <-> 이 레코드들 <-> 라이브러리 UI / 도면 편집기
// ============================================================================
namespace SpatialEditor.Domain;

/// <summary>라이브러리 분류 트리의 노드 하나입니다.</summary>
/// <param name="Id">분류 DB id.</param>
/// <param name="ParentId">상위 분류 id. 최상위 분류이면 null.</param>
/// <param name="Name">분류 이름.</param>
public sealed record BlockCategory(long Id, long? ParentId, string Name);

/// <summary>
/// 전역 라이브러리에 등록된 재사용 가능한 설비 블록 하나입니다. 지오메트리는 블록 자체의
/// 로컬 좌표계(삽입점이 원점)이며 현재 버전에 속합니다.
/// </summary>
/// <param name="Id">라이브러리 항목 DB id.</param>
/// <param name="Code">항목 고유 코드(사용자가 등록 시 입력하는 식별자, 중복 불가. DXF 이관 항목은 블록 이름).</param>
/// <param name="Name">표시 이름.</param>
/// <param name="CategoryId">소속 분류 id. 미분류이면 null.</param>
/// <param name="CategoryName">소속 분류 이름(조회 편의를 위해 조인한 값). 미분류이면 null.</param>
/// <param name="Vendor">제조사. 없으면 null.</param>
/// <param name="ModelNo">모델 번호. 없으면 null.</param>
/// <param name="Description">설명. 없으면 null.</param>
/// <param name="Width">설비 폭(도면 단위). 모르면 null.</param>
/// <param name="Depth">설비 깊이(도면 단위). 모르면 null.</param>
/// <param name="Height">설비 높이(도면 단위). 모르면 null.</param>
/// <param name="Status">상태 문자열. <see cref="LibraryStatus"/> 상수 중 하나.</param>
/// <param name="CurrentVersion">현재 버전 번호(1부터 시작).</param>
/// <param name="InstanceCount">이 항목을 참조하는 도면상 인스턴스 수.</param>
/// <param name="LocalGeometry">블록 원본 지오메트리(로컬 좌표). 없으면 null.</param>
/// <param name="LocalOuterGeometry">블록 외곽(폴리곤화한) 지오메트리(로컬 좌표). 없으면 null.</param>
/// <param name="UpdatedAt">마지막 수정 시각.</param>
public sealed record LibraryItem(
    long Id,
    string Code,
    string Name,
    long? CategoryId,
    string? CategoryName,
    string? Vendor,
    string? ModelNo,
    string? Description,
    double? Width,
    double? Depth,
    double? Height,
    string Status,
    int CurrentVersion,
    int InstanceCount,
    Geometry? LocalGeometry,
    Geometry? LocalOuterGeometry,
    DateTime UpdatedAt);

/// <summary>라이브러리 항목에서 사용자가 편집할 수 있는 카탈로그 필드 모음입니다(지오메트리는 제외).</summary>
/// <param name="Name">새 표시 이름.</param>
/// <param name="CategoryId">새 분류 id. 미분류로 두려면 null.</param>
/// <param name="Vendor">제조사. 비우려면 null.</param>
/// <param name="ModelNo">모델 번호. 비우려면 null.</param>
/// <param name="Description">설명. 비우려면 null.</param>
/// <param name="Height">설비 높이. 모르면 null.</param>
/// <param name="Status">새 상태. <see cref="LibraryStatus"/> 상수 중 하나.</param>
public sealed record LibraryItemEdit(
    string Name,
    long? CategoryId,
    string? Vendor,
    string? ModelNo,
    string? Description,
    double? Height,
    string Status);

/// <summary>도면의 블록을 라이브러리에 등록했을 때의 결과 종류입니다.</summary>
public enum LibraryRegisterOutcome
{
    /// <summary>새 라이브러리 항목(과 버전 1)이 만들어졌습니다.</summary>
    Created,

    /// <summary>동일한 형상의 항목이 이미 있어 새로 만들지 않고 해당 항목에 인스턴스를 연결했습니다.</summary>
    LinkedToExisting
}

/// <summary>블록 등록 결과입니다.</summary>
/// <param name="Outcome">신규 생성인지 기존 항목에 연결인지 구분.</param>
/// <param name="LibraryId">결과적으로 연결된(또는 생성된) 라이브러리 항목 id.</param>
/// <param name="Code">해당 라이브러리 항목의 코드.</param>
public sealed record LibraryRegisterResult(LibraryRegisterOutcome Outcome, long LibraryId, string Code);

/// <summary>라이브러리 항목 상태 문자열 상수입니다(DB 에 그대로 저장됨).</summary>
public static class LibraryStatus
{
    /// <summary>초안: 아직 공개되지 않은 작성 중 상태.</summary>
    public const string Draft = "draft";
    /// <summary>배포됨: 도면에서 사용할 수 있도록 공개된 상태.</summary>
    public const string Released = "released";
    /// <summary>사용 중단: 기존 도면은 유지되지만 신규 사용을 권장하지 않는 상태.</summary>
    public const string Deprecated = "deprecated";

    /// <summary>모든 상태 값(콤보박스 등 목록 표시 순서).</summary>
    public static readonly IReadOnlyList<string> All = new[] { Draft, Released, Deprecated };
}
