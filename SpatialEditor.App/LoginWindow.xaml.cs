using System.Windows;
using System.Windows.Media;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

// ============================================================================
// LoginWindow : 로그인 창 (사용자가 하나도 없으면 "최초 관리자 생성" 모드로 전환)
// ----------------------------------------------------------------------------
// [호출 위치] MainWindow.xaml.cs 의 LoginAsync(): 서버 연결 후
//   var dialog = new LoginWindow(users, connectionSettings.LastLoginId, connectionSettings.LastLoginPassword, setup) { Owner = this };
//   if (dialog.ShowDialog() != true || dialog.User is null) → 로그인 실패/취소 처리.
//   성공하면 dialog.User(로그인한 사용자), dialog.EnteredPassword(입력한 비밀번호, 자동 로그인 저장 등에 사용)를 읽는다.
// [생성자 매개변수]
//   repository    : 사용자 저장소(UserRepository). 인증/최초 관리자 생성에 사용.
//   lastLoginId   : 지난번 로그인 ID(입력칸 미리 채움, 로그인 모드 전용).
//   lastPassword  : 지난번 비밀번호(미리 채움, 로그인 모드 전용; 설정에서 저장하도록 한 경우).
//   setupMode     : true 면 "사용자가 아직 없음" → 최초 관리자(admin) 생성 화면.
// [흐름]
//   열기 → 생성자
//     · 로그인 모드: 안내 문구, 마지막 ID/비밀번호 채움
//     · 설정 모드  : 제목/문구 변경, ID="admin", 이름/비밀번호 확인 칸 표시, 버튼 문구 "Create", 창 높이 확대
//     → Loaded 시 포커스(설정 모드이거나 ID 가 비었으면 ID 칸, 아니면 비밀번호 칸)
//   OK(Ok_Click, IsDefault 이므로 Enter)
//     1) ID/비밀번호 비어 있으면 오류 표시 후 중단
//     2) 설정 모드면 비밀번호 확인 일치 검사
//     3) 창 전체를 비활성화(중복 클릭 방지) 후 비동기 호출
//        · 설정 모드: CreateInitialAdminAsync 로 관리자 생성 → User 설정
//        · 로그인 모드: AuthenticateAsync → 실패 시 비밀번호 칸 비우고 오류 표시(창 유지),
//                       성공 시 User/EnteredPassword 설정
//     4) 성공하면 DialogResult = true ; 예외는 오류 문구로 표시 ; finally 에서 창을 다시 활성화
//   Cancel(IsCancel) → 로그인 취소(호출자가 프로그램 종료/재시도를 결정).
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// 사용자 로그인을 처리한다. 설정(setup) 모드(아직 사용자가 없는 경우)에서는 같은 창이
/// 로그인 대신 최초 관리자 계정을 만든다.
/// </summary>
public partial class LoginWindow : Window
{
    /// <summary>사용자 저장소(인증 및 최초 관리자 생성).</summary>
    private readonly UserRepository repository;
    /// <summary>true 면 최초 관리자 생성 모드, false 면 일반 로그인 모드.</summary>
    private readonly bool setupMode;

    /// <summary>로그인(또는 생성)에 성공한 사용자. 성공 전에는 null.</summary>
    public AppUser? User { get; private set; }

    /// <summary>방금 로그인에 성공한 비밀번호(호출자가 자동 로그인 저장 등에 사용). 로그인 모드에서만 채워진다.</summary>
    public string EnteredPassword { get; private set; } = string.Empty;

    /// <summary>모드(로그인/최초 관리자 생성)에 맞게 창을 구성한다.</summary>
    /// <param name="repository">사용자 저장소.</param>
    /// <param name="lastLoginId">미리 채울 지난번 로그인 ID.</param>
    /// <param name="lastPassword">미리 채울 지난번 비밀번호.</param>
    /// <param name="setupMode">true 면 최초 관리자 생성 모드.</param>
    public LoginWindow(UserRepository repository, string lastLoginId, string lastPassword, bool setupMode)
    {
        InitializeComponent();
        // 1) 의존 객체와 모드 보관.
        this.repository = repository;
        this.setupMode = setupMode;
        // 2) 모드별 화면 구성: 설정 모드는 관리자 생성용 입력 칸을 보이게 하고, 로그인 모드는 마지막 입력값을 채운다.
        if (setupMode)
        {
            Title = "OmniDT SE - Create administrator";
            TitleText.Text = "Create the first administrator";
            MessageText.Text = $"No users exist yet. This account can manage users and roles. Password: at least {PasswordHasher.MinimumLength} characters.";
            LoginIdTextBox.Text = "admin";
            NameLabel.Visibility = NameTextBox.Visibility = Visibility.Visible;
            ConfirmLabel.Visibility = ConfirmInput.Visibility = Visibility.Visible;
            NameTextBox.Text = "Administrator";
            OkButton.Content = "Create";
            Height = 560;
        }
        else
        {
            MessageText.Text = "Log in with your application account.";
            LoginIdTextBox.Text = lastLoginId;
            PasswordInput.Password = lastPassword;
        }

        // 3) 표시되면 입력해야 할 칸에 포커스를 준다(ID 가 있으면 비밀번호 칸).
        Loaded += (_, _) =>
        {
            if (setupMode || string.IsNullOrEmpty(LoginIdTextBox.Text))
            {
                LoginIdTextBox.Focus();
            }
            else
            {
                PasswordInput.Focus();
            }
        };
    }

    /// <summary>안내 문구 영역에 오류 문구를 붉은색으로 표시한다.</summary>
    /// <param name="text">표시할 오류 문구.</param>
    private void ShowError(string text)
    {
        MessageText.Text = text;
        MessageText.Foreground = Brushes.Firebrick;
    }

    /// <summary>[Log in / Create] 버튼 Click 핸들러. 입력 검증 후 인증(또는 관리자 생성)을 수행하고 성공 시 대화상자를 true 로 닫는다.</summary>
    /// <param name="sender">OK 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 1) 필수 입력(ID, 비밀번호) 확인.
        var loginId = LoginIdTextBox.Text.Trim();
        var password = PasswordInput.Password;
        if (loginId.Length == 0 || password.Length == 0)
        {
            ShowError("Enter the ID and password.");
            return;
        }

        // 2) 최초 관리자 생성 시에는 비밀번호 확인 칸이 일치해야 한다.
        if (setupMode && password != ConfirmInput.Password)
        {
            ShowError("The passwords do not match.");
            return;
        }

        // 3) 서버 호출 동안 창을 잠가 중복 제출을 막고, 결과에 따라 사용자 설정/오류 표시.
        IsEnabled = false;
        try
        {
            if (setupMode)
            {
                User = await repository.CreateInitialAdminAsync(loginId, NameTextBox.Text, password);
            }
            else
            {
                var result = await repository.AuthenticateAsync(loginId, password);
                if (result.User is null)
                {
                    PasswordInput.Clear();
                    ShowError(result.Error ?? "Login failed.");
                    return;
                }

                User = result.User;
                EnteredPassword = password;
            }

            DialogResult = true;
        }
        // 정책 위반(비밀번호 규칙 등) 같은 예상 가능한 오류는 메시지를 그대로 보여 준다.
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
        catch (Exception exception)
        {
            ShowError($"Failed: {exception.Message}");
        }
        // 4) 성공/실패와 관계없이 창을 다시 활성화한다(실패 시 재입력 가능).
        finally
        {
            IsEnabled = true;
        }
    }
}
