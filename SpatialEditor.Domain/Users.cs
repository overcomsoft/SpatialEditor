// ============================================================================
// Users.cs : 사용자/역할/권한/감사 로그 도메인 모델
//
//   Permissions   : 권한 키 상수 + 화면 표시용 목록(All) + 최초 실행 시 만들어질 기본 역할(DefaultRoles)
//   AppUser       : 로그인한 사용자. Granted 는 소속 역할들의 권한 키를 합친 집합이며 Has() 로 권한 판정
//   AppRole       : 역할(권한 키 묶음). IsSystem 이면 시스템 기본 역할
//   AuditEntry    : 감사 로그 한 줄
//   AuthResult    : 로그인 시도 결과(성공 시 User, 실패 시 Error 메시지)
//
//   흐름:  로그인 -> UserRepository 가 인증 후 AuthResult 반환 -> 화면은 AppUser.Has(키)로 메뉴/기능 활성화
// ============================================================================
namespace SpatialEditor.Domain;

/// <summary>앱이 검사하는 권한 키 모음입니다. 역할은 이 키들의 집합입니다.</summary>
public static class Permissions
{
    /// <summary>사용자와 역할을 관리할 수 있는 권한.</summary>
    public const string UserManage = "user.manage";
    /// <summary>블록 라이브러리를 조회할 수 있는 권한.</summary>
    public const string LibraryView = "library.view";
    /// <summary>라이브러리에 블록을 등록할 수 있는 권한.</summary>
    public const string LibraryCreate = "library.create";
    /// <summary>라이브러리 블록 정보를 수정할 수 있는 권한.</summary>
    public const string LibraryEdit = "library.edit";
    /// <summary>라이브러리 블록을 삭제할 수 있는 권한.</summary>
    public const string LibraryDelete = "library.delete";
    /// <summary>라이브러리 버전을 배포(공개)할 수 있는 권한.</summary>
    public const string LibraryPublish = "library.publish";
    /// <summary>도면을 조회할 수 있는 권한.</summary>
    public const string DrawingView = "drawing.view";
    /// <summary>도면을 편집(편집 모드 진입, 저장)할 수 있는 권한.</summary>
    public const string DrawingEdit = "drawing.edit";
    /// <summary>도면 객체를 삭제할 수 있는 권한.</summary>
    public const string DrawingDelete = "drawing.delete";
    /// <summary>도면을 내보낼(DXF 등) 수 있는 권한.</summary>
    public const string DrawingExport = "drawing.export";
    /// <summary>DXF 데이터를 가져오거나 삭제할 수 있는 권한.</summary>
    public const string ImportDxf = "import.dxf";
    /// <summary>감사 로그를 조회할 수 있는 권한.</summary>
    public const string AuditView = "audit.view";

    /// <summary>
    /// 모든 권한 키와 표시 이름, 그룹입니다. 역할 편집 화면에 나열되는 순서와 같습니다.
    /// 각 항목은 (Key: 권한 키, Label: 화면 표시 문구, Group: 화면 그룹 제목)입니다.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Group)> All = new[]
    {
        (UserManage, "Manage users and roles", "Users"),
        (LibraryView, "View the block library", "Library"),
        (LibraryCreate, "Register library blocks", "Library"),
        (LibraryEdit, "Edit library blocks", "Library"),
        (LibraryDelete, "Delete library blocks", "Library"),
        (LibraryPublish, "Publish library versions", "Library"),
        (DrawingView, "View drawings", "Drawings"),
        (DrawingEdit, "Edit drawings (edit mode, save)", "Drawings"),
        (DrawingDelete, "Delete drawing objects", "Drawings"),
        (DrawingExport, "Export drawings", "Drawings"),
        (ImportDxf, "Import / delete DXF data", "Data"),
        (AuditView, "View the audit log", "Data")
    };

    /// <summary>
    /// 최초 실행 시 만들어지는 기본 역할입니다. 관리자는 이후 권한을 바꿀 수 있습니다.
    /// 각 항목은 (Name: 역할 이름, Description: 설명, Keys: 부여되는 권한 키 배열)입니다.
    /// Admin 은 모든 권한, 나머지는 업무 범위에 맞게 일부만 가집니다.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Description, string[] Keys)> DefaultRoles = new[]
    {
        ("Admin", "Full access", All.Select(item => item.Key).ToArray()),
        ("LibraryManager", "Manages the block library", new[]
        {
            LibraryView, LibraryCreate, LibraryEdit, LibraryDelete, LibraryPublish,
            DrawingView, DrawingExport, ImportDxf
        }),
        ("Designer", "Creates and edits drawings", new[]
        {
            LibraryView, DrawingView, DrawingEdit, DrawingDelete, DrawingExport, ImportDxf
        }),
        ("Viewer", "Read-only access", new[] { LibraryView, DrawingView })
    };
}

/// <summary>로그인한 사용자 한 명과 그가 가진 권한입니다.</summary>
/// <param name="Id">사용자 DB id.</param>
/// <param name="LoginId">로그인 아이디.</param>
/// <param name="DisplayName">화면에 표시할 이름.</param>
/// <param name="IsActive">계정 사용 가능 여부. false 이면 로그인할 수 없음.</param>
/// <param name="MustChangePassword">true 이면 다음 로그인 시 비밀번호를 반드시 변경해야 함(초기화 직후 등).</param>
/// <param name="LockedUntil">로그인 실패 누적으로 잠긴 경우 잠금 해제 시각(UTC 기준). 잠기지 않았으면 null.</param>
/// <param name="LastLoginAt">마지막 로그인 성공 시각. 기록 없으면 null.</param>
/// <param name="Roles">소속 역할 이름 목록.</param>
/// <param name="Granted">소속 역할들의 권한 키를 합친 집합(권한 판정에 사용).</param>
public sealed record AppUser(
    long Id,
    string LoginId,
    string DisplayName,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LockedUntil,
    DateTime? LastLoginAt,
    IReadOnlyList<string> Roles,
    IReadOnlySet<string> Granted)
{
    /// <summary>사용자가 해당 권한을 가졌는지 확인합니다.</summary>
    /// <param name="permission">확인할 권한 키(<see cref="Permissions"/> 상수).</param>
    /// <returns>가졌으면 true.</returns>
    public bool Has(string permission) => Granted.Contains(permission);
}

/// <summary>역할(권한 키 묶음)입니다.</summary>
/// <param name="Id">역할 DB id.</param>
/// <param name="Name">역할 이름(고유).</param>
/// <param name="Description">설명. 없으면 null.</param>
/// <param name="IsSystem">true 이면 기본 제공 시스템 역할(삭제/이름 변경 제한 대상).</param>
/// <param name="PermissionKeys">이 역할에 부여된 권한 키 목록.</param>
/// <param name="UserCount">이 역할을 가진 사용자 수.</param>
public sealed record AppRole(
    long Id,
    string Name,
    string? Description,
    bool IsSystem,
    IReadOnlyList<string> PermissionKeys,
    int UserCount);

/// <summary>감사 로그 한 줄입니다.</summary>
/// <param name="Id">로그 DB id.</param>
/// <param name="At">발생 시각.</param>
/// <param name="UserName">행위자 이름. 시스템/미로그인이면 null.</param>
/// <param name="Action">수행한 작업 코드/이름.</param>
/// <param name="EntityType">작업 대상 종류. 해당 없으면 null.</param>
/// <param name="EntityId">작업 대상 식별자(문자열). 해당 없으면 null.</param>
/// <param name="Summary">사람이 읽을 요약 설명. 없으면 null.</param>
public sealed record AuditEntry(
    long Id,
    DateTime At,
    string? UserName,
    string Action,
    string? EntityType,
    string? EntityId,
    string? Summary);

/// <summary>로그인 시도 결과입니다. 성공하면 User, 실패하면 화면에 보여줄 Error 메시지가 채워집니다.</summary>
/// <param name="User">인증된 사용자. 실패하면 null.</param>
/// <param name="Error">실패 사유 메시지. 성공하면 null.</param>
public sealed record AuthResult(AppUser? User, string? Error);
