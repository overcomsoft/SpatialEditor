using System.Windows;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

// ============================================================================
// UserManagementWindow : 사용자/역할/감사 로그 관리 창 (TabControl 3개 탭)
// ----------------------------------------------------------------------------
// [호출 위치] MainWindow.xaml.cs ManageUsers_Click (메뉴 "User management...")
//   new UserManagementWindow(users, currentUser) { Owner = this }.ShowDialog()
// [생성자 매개변수]
//   repository : 사용자 저장소(UserRepository). 사용자/역할/감사 조회와 변경.
//   actor      : 작업을 수행하는 관리자(AppUser). 권한 확인과 감사 기록의 "누가"로 쓰인다.
// [탭 구성]
//   Users 탭     : UsersGrid + Add / Edit / Reset password / Unlock / Delete / Refresh (행 더블클릭 = Edit)
//   Roles 탭     : RolesGrid + Add / Edit / Delete / Refresh (행 더블클릭 = Edit)
//   Audit log 탭 : AuditGrid + Refresh (actor 에게 Permissions.AuditView 권한이 있을 때만 탭 표시)
// [공통 흐름]
//   열기 → 생성자: 감사 탭 표시 여부 결정 → Loaded 시 ReloadAsync (역할/사용자/(감사) 조회 후 그리드에 바인딩)
//   각 버튼 핸들러는 같은 패턴을 따른다:
//     1) 선택 행 확인(필요한 경우) → 2) 편집/확인 대화상자(UserEditWindow / RoleEditWindow / PasswordWindow / MessageBox)
//     → 취소하면 종료 → 3) 저장소 호출(예외는 ShowError 로 표시) → 4) ReloadAsync 로 목록 새로고침
//   · Add/Edit User  : UserEditWindow → CreateUserAsync / UpdateUserAsync
//   · Reset password : PasswordWindow(askOld:false, offerMustChange:true) → ResetPasswordAsync
//   · Unlock         : UnlockUserAsync(로그인 실패로 잠긴 계정 해제)
//   · Delete User    : 확인 → DeleteUserAsync (활동 이력이 있으면 서버가 거부 → 비활성화 권장)
//   · Add/Edit/Delete Role : RoleEditWindow / 확인 → CreateRoleAsync / UpdateRoleAsync / DeleteRoleAsync
//   DialogResult 는 사용하지 않는다(변경은 즉시 서버에 반영).
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// 관리자가 사용자를 추가/수정/비활성화/삭제하고, 비밀번호를 초기화하고, 잠금을 해제하며,
/// 역할과 권한을 정의하는 창. 감사 탭에는 최근 보안 관련 이벤트가 표시된다.
/// </summary>
public partial class UserManagementWindow : Window
{
    /// <summary>Users 그리드 한 줄(바인딩용 읽기 전용 표현). 원본 사용자 정보를 화면 문자열로 변환한다.</summary>
    /// <param name="User">원본 사용자.</param>
    private sealed record UserRow(AppUser User)
    {
        /// <summary>로그인 ID.</summary>
        public string LoginId => User.LoginId;
        /// <summary>표시 이름.</summary>
        public string DisplayName => User.DisplayName;
        /// <summary>보유 역할 이름을 쉼표로 이은 문자열.</summary>
        public string Roles => string.Join(", ", User.Roles);
        /// <summary>활성 여부("Yes"/"No").</summary>
        public string Active => User.IsActive ? "Yes" : "No";
        /// <summary>상태 문구: 잠금 중이면 "Locked until HH:mm", 비밀번호 변경 필요면 "Must change password", 아니면 빈 문자열.</summary>
        public string Status => User.LockedUntil is { } until && until > DateTime.UtcNow
            ? $"Locked until {until.ToLocalTime():HH:mm}"
            : User.MustChangePassword ? "Must change password" : string.Empty;
        /// <summary>마지막 로그인 시각(로컬, yyyy-MM-dd HH:mm). 없으면 "-".</summary>
        public string LastLogin => User.LastLoginAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
    }

    /// <summary>Roles 그리드 한 줄(바인딩용). 원본 역할 정보를 화면 문자열/숫자로 변환한다.</summary>
    /// <param name="Role">원본 역할.</param>
    private sealed record RoleRow(AppRole Role)
    {
        /// <summary>역할 이름.</summary>
        public string Name => Role.Name;
        /// <summary>역할 설명(없으면 빈 문자열).</summary>
        public string Description => Role.Description ?? string.Empty;
        /// <summary>시스템 기본 제공 역할이면 "Yes".</summary>
        public string BuiltIn => Role.IsSystem ? "Yes" : string.Empty;
        /// <summary>이 역할을 가진 사용자 수.</summary>
        public int UserCount => Role.UserCount;
        /// <summary>이 역할에 부여된 권한 수.</summary>
        public int PermissionCount => Role.PermissionKeys.Count;
    }

    /// <summary>Audit log 그리드 한 줄(바인딩용). 감사 항목을 화면 문자열로 변환한다.</summary>
    /// <param name="Entry">원본 감사 항목.</param>
    private sealed record AuditRow(AuditEntry Entry)
    {
        /// <summary>발생 시각(로컬, yyyy-MM-dd HH:mm:ss).</summary>
        public string Time => Entry.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        /// <summary>수행한 사용자 이름(없으면 "-").</summary>
        public string User => Entry.UserName ?? "-";
        /// <summary>동작 이름(예: library.edit).</summary>
        public string Action => Entry.Action;
        /// <summary>대상 종류와 ID(없으면 빈 문자열).</summary>
        public string Target => string.IsNullOrEmpty(Entry.EntityType) ? string.Empty : $"{Entry.EntityType} {Entry.EntityId}";
        /// <summary>상세 요약(없으면 빈 문자열).</summary>
        public string Detail => Entry.Summary ?? string.Empty;
    }

    /// <summary>사용자 저장소.</summary>
    private readonly UserRepository repository;
    /// <summary>작업을 수행하는 로그인 관리자(권한 확인/감사 기록용).</summary>
    private readonly AppUser actor;
    /// <summary>마지막으로 불러온 역할 목록(사용자 편집 창에 전달).</summary>
    private IReadOnlyList<AppRole> roles = Array.Empty<AppRole>();

    /// <summary>사용자 관리 창을 만든다.</summary>
    /// <param name="repository">사용자 저장소.</param>
    /// <param name="actor">작업 수행 관리자.</param>
    public UserManagementWindow(UserRepository repository, AppUser actor)
    {
        InitializeComponent();
        this.repository = repository;
        this.actor = actor;
        // 감사 로그 열람 권한이 없으면 탭을 숨기고, 창이 표시된 뒤 비동기로 데이터를 불러온다.
        AuditTab.Visibility = actor.Has(Permissions.AuditView) ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>역할, 사용자, (권한이 있으면) 감사 로그를 서버에서 다시 읽어 각 그리드에 바인딩한다. 오류는 ShowError 로 표시한다.</summary>
    private async Task ReloadAsync()
    {
        try
        {
            roles = await repository.ListRolesAsync();
            RolesGrid.ItemsSource = roles.Select(role => new RoleRow(role)).ToList();
            UsersGrid.ItemsSource = (await repository.ListUsersAsync()).Select(user => new UserRow(user)).ToList();
            if (actor.Has(Permissions.AuditView))
            {
                AuditGrid.ItemsSource = (await repository.ListAuditAsync()).Select(entry => new AuditRow(entry)).ToList();
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>예외 메시지를 대화상자로 보여 준다(정책 위반 같은 InvalidOperationException 은 경고, 그 외는 오류 아이콘).</summary>
    /// <param name="exception">표시할 예외.</param>
    private void ShowError(Exception exception) =>
        MessageBox.Show(this, exception.Message, "User management", MessageBoxButton.OK,
            exception is InvalidOperationException ? MessageBoxImage.Warning : MessageBoxImage.Error);

    /// <summary>Users 그리드에서 선택된 사용자(없으면 null).</summary>
    private AppUser? SelectedUser => (UsersGrid.SelectedItem as UserRow)?.User;
    /// <summary>Roles 그리드에서 선택된 역할(없으면 null).</summary>
    private AppRole? SelectedRole => (RolesGrid.SelectedItem as RoleRow)?.Role;

    /// <summary>[Refresh] 버튼 Click 핸들러(세 탭 공용). 목록을 다시 불러온다.</summary>
    /// <param name="sender">Refresh 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    // ----------------------------- 사용자 탭 -----------------------------

    /// <summary>[Add] 버튼(Users 탭) Click 핸들러. 사용자 편집 창으로 입력받아 새 사용자를 만든다.</summary>
    /// <param name="sender">Add 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void AddUser_Click(object sender, RoutedEventArgs e)
    {
        // 신규 모드(existing = null)로 입력받고, 확인되면 서버에 생성한다(오류는 표시 후에도 목록은 새로고침).
        var dialog = new UserEditWindow(roles, null) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await repository.CreateUserAsync(actor, dialog.LoginId, dialog.DisplayName, dialog.Password,
                dialog.MustChangePassword, dialog.SelectedRoleIds);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    /// <summary>[Edit] 버튼 또는 Users 그리드 더블클릭(MouseDoubleClick) 핸들러. 선택한 사용자의 이름/활성 여부/역할을 수정한다.</summary>
    /// <param name="sender">Edit 버튼 또는 UsersGrid.</param>
    /// <param name="e">라우팅 이벤트 인자(더블클릭 시 MouseButtonEventArgs).</param>
    private async void EditUser_Click(object sender, RoutedEventArgs e)
    {
        // 1) 선택된 사용자가 없으면 아무 것도 하지 않는다. 2) 수정 창 → 3) UpdateUserAsync → 4) 재조회.
        if (SelectedUser is not { } user)
        {
            return;
        }

        var dialog = new UserEditWindow(roles, user) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await repository.UpdateUserAsync(actor, user.Id, dialog.DisplayName, dialog.UserIsActive, dialog.SelectedRoleIds);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    /// <summary>[Reset password] 버튼 Click 핸들러. 선택한 사용자의 비밀번호를 관리자가 새로 지정한다(현재 비밀번호 불필요).</summary>
    /// <param name="sender">Reset password 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUser is not { } user)
        {
            return;
        }

        // 현재 비밀번호 칸 없이, "다음 로그인 때 변경 요구" 옵션을 제공하는 모드로 입력받는다.
        var dialog = new PasswordWindow("Reset password", $"Set a new password for '{user.LoginId}'.", askOld: false, offerMustChange: true)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await repository.ResetPasswordAsync(actor, user.Id, dialog.NewPassword, dialog.MustChange);
            MessageBox.Show(this, $"The password of '{user.LoginId}' was reset.", "User management");
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    /// <summary>[Unlock] 버튼 Click 핸들러. 로그인 실패로 잠긴 계정의 잠금을 해제한다.</summary>
    /// <param name="sender">Unlock 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void UnlockUser_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUser is not { } user)
        {
            return;
        }

        try
        {
            await repository.UnlockUserAsync(actor, user.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    /// <summary>[Delete] 버튼(Users 탭) Click 핸들러. 확인 후 선택한 사용자를 영구 삭제한다(활동 이력이 있으면 서버가 거부).</summary>
    /// <param name="sender">Delete 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUser is not { } user)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Permanently delete user '{user.LoginId}'?\n\nUsers with activity history cannot be deleted; deactivate them instead (Edit).",
            "Delete user", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await repository.DeleteUserAsync(actor, user.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    // ----------------------------- 역할 탭 -----------------------------

    /// <summary>[Add] 버튼(Roles 탭) Click 핸들러. 역할 편집 창으로 입력받아 새 역할을 만든다.</summary>
    /// <param name="sender">Add 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void AddRole_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RoleEditWindow(null) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await repository.CreateRoleAsync(actor, dialog.RoleName, dialog.Description, dialog.SelectedPermissions);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    /// <summary>[Edit] 버튼 또는 Roles 그리드 더블클릭 핸들러. 선택한 역할의 이름/설명/권한을 수정한다.</summary>
    /// <param name="sender">Edit 버튼 또는 RolesGrid.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void EditRole_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRole is not { } role)
        {
            return;
        }

        var dialog = new RoleEditWindow(role) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await repository.UpdateRoleAsync(actor, role.Id, dialog.RoleName, dialog.Description, dialog.SelectedPermissions);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    /// <summary>[Delete] 버튼(Roles 탭) Click 핸들러. 확인 후 선택한 역할을 삭제한다.</summary>
    /// <param name="sender">Delete 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void DeleteRole_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRole is not { } role)
        {
            return;
        }

        var answer = MessageBox.Show(this, $"Delete the role '{role.Name}'?", "Delete role",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await repository.DeleteRoleAsync(actor, role.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }
}
