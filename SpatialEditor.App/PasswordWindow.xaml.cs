using System.Windows;
using System.Windows.Media;
using SpatialEditor.Infrastructure;

// ============================================================================
// PasswordWindow : 새 비밀번호 입력 대화상자 (본인 변경 / 관리자 초기화 / 로그인 시 강제 변경 겸용)
// ----------------------------------------------------------------------------
// [호출 위치]
//   1) MainWindow.xaml.cs LoginAsync : 로그인 직후 "비밀번호 변경 필수" 계정
//        new PasswordWindow("Change password", "You must set a new password before continuing.", askOld: true, allowCancel: false)
//   2) MainWindow.xaml.cs ChangePassword_Click (메뉴 "Change my password...")
//        new PasswordWindow("Change password", "Choose a new password.", askOld: true)
//   3) UserManagementWindow.ResetPassword_Click (관리자가 다른 사용자 비밀번호 초기화)
//        new PasswordWindow("Reset password", ..., askOld: false, offerMustChange: true)
// [생성자 매개변수]
//   title           : 창 제목.
//   message         : 안내 문구("(at least N characters)" 가 자동으로 덧붙는다).
//   askOld          : true 면 "현재 비밀번호" 칸 표시(본인 변경). false 면 숨기고 창 높이를 줄임(관리자 초기화).
//   allowCancel     : false 면 Cancel 버튼 숨김(로그인 시 강제 변경). 기본 true.
//   offerMustChange : true 면 "다음 로그인 때 변경 요구" 체크박스를 보이고 기본 체크(관리자 초기화).
// [흐름]
//   열기 → 생성자에서 제목/문구/표시 요소 설정 → Loaded 시 첫 입력 칸(현재 비밀번호 또는 새 비밀번호)에 포커스
//   OK(Ok_Click) → 1) PasswordHasher.ValidatePolicy 로 정책(최소 길이 등) 검사, 위반이면 문구 표시 후 유지
//                  2) 새 비밀번호와 확인 칸 일치 검사, 불일치면 문구 표시 후 유지
//                  3) 통과하면 DialogResult = true
//   호출자는 true 일 때 OldPassword / NewPassword / MustChange 를 읽어 실제 변경을 수행한다.
//   (이 창 자체는 서버에 아무것도 저장하지 않는다.)
// ============================================================================
namespace SpatialEditor.App;

/// <summary>새 비밀번호를 입력받는다: 본인 변경(현재 비밀번호 요구) 또는 관리자 초기화.</summary>
public partial class PasswordWindow : Window
{
    /// <summary>입력된 현재 비밀번호(askOld=false 면 의미 없음).</summary>
    public string OldPassword => OldInput.Password;
    /// <summary>입력된 새 비밀번호.</summary>
    public string NewPassword => NewInput.Password;
    /// <summary>"다음 로그인 때 변경 요구" 체크 여부(관리자 초기화 시 사용).</summary>
    public bool MustChange => MustChangeCheckBox.IsChecked == true;

    /// <summary>비밀번호 창을 구성한다.</summary>
    /// <param name="title">창 제목.</param>
    /// <param name="message">안내 문구(최소 길이 문구가 자동 추가됨).</param>
    /// <param name="askOld">현재 비밀번호를 요구할지(본인 변경이면 true).</param>
    /// <param name="allowCancel">Cancel 버튼 표시 여부(로그인 시 강제 변경이면 false).</param>
    /// <param name="offerMustChange">"다음 로그인 때 변경 요구" 옵션 표시 여부(관리자 초기화이면 true).</param>
    public PasswordWindow(string title, string message, bool askOld, bool allowCancel = true, bool offerMustChange = false)
    {
        InitializeComponent();
        // 1) 제목/문구와 옵션별 표시 요소(현재 비밀번호 칸, Cancel 버튼, 변경 요구 체크)를 설정한다.
        Title = title;
        MessageText.Text = $"{message} (at least {PasswordHasher.MinimumLength} characters)";
        OldLabel.Visibility = OldInput.Visibility = askOld ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
        MustChangeCheckBox.Visibility = offerMustChange ? Visibility.Visible : Visibility.Collapsed;
        MustChangeCheckBox.IsChecked = offerMustChange;
        // 2) 현재 비밀번호 칸이 없으면 창 높이를 줄인다.
        if (!askOld)
        {
            Height = 290;
        }

        // 3) 표시되면 입력해야 할 첫 칸에 포커스를 준다.
        Loaded += (_, _) => (askOld ? OldInput : NewInput).Focus();
    }

    /// <summary>[OK] 버튼 Click 핸들러. 비밀번호 정책과 확인 일치 여부를 검사하고 통과하면 true 로 닫는다.</summary>
    /// <param name="sender">OK 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 1) 정책(최소 길이 등) 위반이면 사유를 붉은색으로 표시하고 창을 유지한다.
        if (PasswordHasher.ValidatePolicy(NewInput.Password) is { } problem)
        {
            MessageText.Text = problem;
            MessageText.Foreground = Brushes.Firebrick;
            return;
        }

        // 2) 새 비밀번호와 확인 입력이 같아야 한다.
        if (NewInput.Password != ConfirmInput.Password)
        {
            MessageText.Text = "The passwords do not match.";
            MessageText.Foreground = Brushes.Firebrick;
            return;
        }

        // 3) 통과.
        DialogResult = true;
    }
}
