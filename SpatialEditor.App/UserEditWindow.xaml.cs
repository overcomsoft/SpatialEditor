using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpatialEditor.Domain;

// ============================================================================
// UserEditWindow : 사용자 추가/수정 대화상자 (ID, 이름, 활성 여부, 초기 비밀번호, 역할 체크 목록)
// ----------------------------------------------------------------------------
// [호출 위치] UserManagementWindow.xaml.cs
//   · AddUser_Click  : new UserEditWindow(roles, null) { Owner = this }  → 신규 사용자(초기 비밀번호 입력)
//   · EditUser_Click : new UserEditWindow(roles, user) { Owner = this }  → 기존 사용자 수정(ID/비밀번호 변경 불가)
// [생성자 매개변수]
//   roles    : 선택 가능한 전체 역할 목록(AppRole). 역할마다 체크박스가 만들어진다.
//   existing : 수정할 사용자. null 이면 신규 추가 모드.
// [흐름]
//   열기 → 신규 여부(isNew) 판단 → 제목 설정("Add user" / "Edit user: ID")
//     → 역할마다 CheckBox 생성(수정 시 사용자가 이미 가진 역할 이름은 체크)
//     → 수정 모드면 ID 칸(잠금)/이름/활성 여부를 채우고 비밀번호 패널을 숨기며 창 높이 축소
//   OK(Ok_Click) → 신규일 때 비밀번호와 확인 칸이 다르면 오류 표시 후 유지, 아니면 DialogResult = true
//     (비밀번호 정책 등 나머지 검증은 호출자가 저장소 호출 시 수행하고 오류를 보여 준다.)
//   Cancel(IsCancel) → 변경 없이 닫힘.
//   호출자는 true 일 때 LoginId/DisplayName/UserIsActive/Password/MustChangePassword/SelectedRoleIds 를 읽는다.
// ============================================================================
namespace SpatialEditor.App;

/// <summary>사용자를 추가(초기 비밀번호 포함)하거나 기존 사용자를 수정(이름, 활성 여부, 역할)한다.</summary>
public partial class UserEditWindow : Window
{
    /// <summary>역할 ID → 해당 체크박스 매핑(선택된 역할 수집용).</summary>
    private readonly Dictionary<long, CheckBox> roleChecks = new();
    /// <summary>신규 사용자 추가 모드이면 true(비밀번호 확인 검증 대상).</summary>
    private readonly bool isNew;

    /// <summary>입력된 로그인 ID(앞뒤 공백 제거).</summary>
    public string LoginId => LoginIdTextBox.Text.Trim();
    /// <summary>입력된 표시 이름(앞뒤 공백 제거).</summary>
    public string DisplayName => NameTextBox.Text.Trim();
    /// <summary>"Active (can log in)" 체크 여부. false 면 로그인 불가 계정.</summary>
    public bool UserIsActive => ActiveCheckBox.IsChecked == true;
    /// <summary>입력된 초기 비밀번호(신규 모드에서만 의미 있음).</summary>
    public string Password => PasswordInput.Password;
    /// <summary>"첫 로그인 때 변경 요구" 체크 여부(신규 모드).</summary>
    public bool MustChangePassword => MustChangeCheckBox.IsChecked == true;
    /// <summary>체크된 역할의 ID 목록.</summary>
    public IReadOnlyList<long> SelectedRoleIds => roleChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();

    /// <summary>사용자 편집 창을 구성한다.</summary>
    /// <param name="roles">선택 가능한 전체 역할 목록.</param>
    /// <param name="existing">수정할 기존 사용자. null 이면 신규 추가 모드.</param>
    public UserEditWindow(IReadOnlyList<AppRole> roles, AppUser? existing)
    {
        InitializeComponent();
        // 1) 모드에 맞게 제목 설정.
        isNew = existing is null;
        Title = isNew ? "Add user" : $"Edit user: {existing!.LoginId}";
        // 2) 역할마다 체크박스를 만든다(설명이 있으면 "이름 - 설명"). 수정 시 이미 가진 역할은 체크.
        foreach (var role in roles)
        {
            var check = new CheckBox
            {
                Content = string.IsNullOrWhiteSpace(role.Description) ? role.Name : $"{role.Name} - {role.Description}",
                IsChecked = existing?.Roles.Contains(role.Name) == true,
                Margin = new Thickness(0, 2, 0, 2)
            };
            roleChecks[role.Id] = check;
            RolesPanel.Children.Add(check);
        }

        // 3) 수정 모드: ID 는 변경할 수 없도록 잠그고, 비밀번호 입력은 숨긴다(비밀번호 초기화는 별도 기능).
        if (existing is not null)
        {
            LoginIdTextBox.Text = existing.LoginId;
            LoginIdTextBox.IsEnabled = false;
            NameTextBox.Text = existing.DisplayName;
            ActiveCheckBox.IsChecked = existing.IsActive;
            PasswordPanel.Visibility = Visibility.Collapsed;
            Height = 420;
        }
    }

    /// <summary>[OK] 버튼 Click 핸들러. 신규 모드에서 비밀번호/확인이 일치하는지 검사하고 통과하면 true 로 닫는다.</summary>
    /// <param name="sender">OK 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (isNew && PasswordInput.Password != ConfirmInput.Password)
        {
            MessageText.Text = "The passwords do not match.";
            MessageText.Foreground = Brushes.Firebrick;
            return;
        }

        DialogResult = true;
    }
}
