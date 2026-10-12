using System.Windows;
using System.Windows.Media;
using SpatialEditor.Infrastructure;

// ============================================================================
// ServerConnectionWindow : PostgreSQL(PostGIS) 서버 연결 정보 입력/검증 대화상자
// ----------------------------------------------------------------------------
// [호출 위치] MainWindow.xaml.cs ShowServerDialog(message)
//   var dialog = new ServerConnectionWindow(connectionSettings, message) { Owner = this };
//   return dialog.ShowDialog() == true ? dialog.Settings : null;
//   (메뉴 "Connect to server..." 또는 시작 시 자동 연결 실패 때 표시)
// [생성자 매개변수]
//   initial : 현재/이전 연결 설정(호스트, 포트, DB, 사용자, 비밀번호 + 선택 레이어/레이어 스타일/마지막 로그인 ID).
//   message : 선택. 있으면 붉은색 오류로 안내(예: 자동 연결 실패 사유).
// [흐름]
//   열기 → 입력 칸(Host/Port/Database/Username/Password)을 initial 값으로 채움 → message 가 있으면 표시
//   Connect(Connect_Click, Enter)
//     1) 포트가 1~65535 숫자인지 검사
//     2) Host/Database/Username 필수 검사
//     3) 입력값으로 새 PostgresConnectionSettings(candidate) 구성
//        (이 창은 연결 정보만 편집하므로 기존 SelectedLayers/LayerStyles/LastLoginId 는 그대로 복사)
//     4) 버튼/창 비활성화 → "Connecting..." → PostGisFeatureRepository.TestConnectionAsync (실제 접속 + PostGIS 확인)
//     5) 성공: candidate.SaveAsync()로 설정 JSON 저장 → Settings = candidate → DialogResult = true
//        실패: 창 재활성화, "Connection failed: ..." 표시(창 유지)
//   Cancel(IsCancel) → Settings 는 null, 호출자는 연결 취소로 처리.
//   ※ 실제 연결에 성공한 경우에만 true 로 닫힌다.
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// PostgreSQL 연결 정보를 입력받고, 실제 연결(PostGIS 확인 포함)에 성공해야만 성공(true)으로 닫힌다.
/// 연결에 성공한 설정은 설정 JSON 에 저장되어 다음 실행 때 자동으로 연결할 수 있다.
/// </summary>
public partial class ServerConnectionWindow : Window
{
    /// <summary>창을 열 때 전달받은 설정(연결과 무관한 값인 선택 레이어/스타일/로그인 ID 를 보존하기 위해 보관).</summary>
    private readonly PostgresConnectionSettings current;

    /// <summary>연결에 성공한 설정(대화상자가 true 로 닫힐 때 설정됨).</summary>
    public PostgresConnectionSettings? Settings { get; private set; }

    /// <summary>초기 설정으로 입력 칸을 채우고, 안내 문구가 있으면 오류로 표시한다.</summary>
    /// <param name="initial">입력 칸에 미리 채울 연결 설정.</param>
    /// <param name="message">선택 안내/오류 문구(null 이면 표시하지 않음).</param>
    public ServerConnectionWindow(PostgresConnectionSettings initial, string? message = null)
    {
        InitializeComponent();
        // 1) 전달받은 설정을 보관하고 입력 칸에 채운다.
        current = initial;
        HostTextBox.Text = initial.Host;
        PortTextBox.Text = initial.Port.ToString();
        DatabaseTextBox.Text = initial.Database;
        UsernameTextBox.Text = initial.Username;
        PasswordInput.Password = initial.Password;
        // 2) 호출자가 알려 준 사유(예: 이전 연결 실패)가 있으면 붉게 표시한다.
        if (message is not null)
        {
            ShowMessage(message, isError: true);
        }
    }

    /// <summary>안내 영역에 문구를 표시한다(오류는 붉은색, 정상/진행은 초록색).</summary>
    /// <param name="text">표시할 문구.</param>
    /// <param name="isError">true 면 오류 색, false 면 정상 색.</param>
    private void ShowMessage(string text, bool isError)
    {
        MessageText.Text = text;
        MessageText.Foreground = isError ? Brushes.Firebrick : Brushes.SeaGreen;
    }

    /// <summary>[Connect] 버튼 Click 핸들러. 입력을 검증하고 실제로 접속해 본 뒤, 성공하면 설정을 저장하고 true 로 닫는다.</summary>
    /// <param name="sender">Connect 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        // 1) 포트 번호 검증(1~65535).
        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            ShowMessage("Port must be a number between 1 and 65535.", isError: true);
            return;
        }

        // 2) 호스트, 데이터베이스, 사용자 이름은 필수.
        if (string.IsNullOrWhiteSpace(HostTextBox.Text) || string.IsNullOrWhiteSpace(DatabaseTextBox.Text)
            || string.IsNullOrWhiteSpace(UsernameTextBox.Text))
        {
            ShowMessage("Host, database and username are required.", isError: true);
            return;
        }

        // 3) 입력값으로 연결 후보 설정을 만든다.
        var candidate = new PostgresConnectionSettings
        {
            Host = HostTextBox.Text.Trim(),
            Port = port,
            Database = DatabaseTextBox.Text.Trim(),
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordInput.Password,
            // The dialog only edits the connection; keep the saved layer choice and styles.
            SelectedLayers = current.SelectedLayers,
            LayerStyles = current.LayerStyles,
            LastLoginId = current.LastLoginId
        };

        // 4) 접속 시도 중에는 중복 클릭을 막는다.
        ConnectButton.IsEnabled = false;
        IsEnabled = false;
        ShowMessage("Connecting...", isError: false);
        try
        {
            // 5) 실제 접속 테스트(PostGIS 확인 포함) → 성공 시 설정 저장 후 성공으로 닫는다.
            await using var repository = new PostGisFeatureRepository(candidate.ConnectionString);
            var info = await repository.TestConnectionAsync();
            await candidate.SaveAsync();
            Settings = candidate;
            ShowMessage(info, isError: false);
            DialogResult = true;
        }
        // 6) 실패하면 다시 입력할 수 있도록 창을 활성화하고 사유를 표시한다.
        catch (Exception exception)
        {
            IsEnabled = true;
            ConnectButton.IsEnabled = true;
            ShowMessage($"Connection failed: {exception.Message}", isError: true);
        }
    }
}
