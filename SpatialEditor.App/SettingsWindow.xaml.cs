using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

// ============================================================================
// SettingsWindow : 환경 설정 대화상자 (왼쪽 목록 PageList 로 3개 페이지 전환)
// ----------------------------------------------------------------------------
// [호출 위치] MainWindow.Settings.cs OpenSettings_Click (메뉴 "Settings...")
//   var dialog = new SettingsWindow(appSettings, shortcutCommands, connectionSettings.LastLoginId,
//                                   connectionSettings.LastLoginPassword.Length > 0) { Owner = this };
//   if (dialog.ShowDialog() != true) return;  → true 이면 appSettings = dialog.Settings; appSettings.Save();
//   dialog.ClearSavedPassword 가 true 이면 호출자가 저장된 비밀번호를 삭제한다.
// [생성자 매개변수]
//   settings         : 현재 앱 설정. 창은 Clone() 한 사본(Settings)을 편집하므로 Cancel 하면 원본은 그대로.
//   commands         : 단축키를 지정할 수 있는 기능(ShortcutCommand) 목록(ID, 분류, 이름, 기본 단축키).
//   savedLoginId     : 저장된 로그인 ID(없으면 빈 문자열). 안내 문구에 표시.
//   hasSavedPassword : 저장된 비밀번호가 있는지(삭제 버튼 활성 및 안내 문구).
// [페이지 구성] PageList 의 SelectedIndex 로 전환(PageList_SelectionChanged)
//   0) 로그인 페이지(LoginPage)  : 비밀번호 기억(RememberPasswordCheck), 자동 로그인(AutoLoginCheck), 저장 비밀번호 삭제
//   1) 단축키 페이지(ShortcutPage): 기능 목록(ShortcutList) + 키 캡처 상자(CaptureBox) + Clear/Default/Reset all
//   2) 일반 페이지(GeneralPage)   : 편집 자동 저장/주기, 저장 시 자동 버전, 선택 외곽선 색/두께 + 미리보기
// [흐름]
//   열기 → 생성자: 설정 사본 생성 → 각 컨트롤에 현재 값 채움(로그인 옵션, 단축키 행, 일반 옵션, 선택 외곽선)
//     → 의존 활성 상태 갱신(UpdateLoginOptions, UpdateAutoSavePanel, UpdateSelectionPreview)
//   사용자 동작
//     · 로그인: 기억 해제 시 자동 로그인 비활성 → LoginOptions_Changed ; 삭제 버튼 → ClearPassword_Click (ClearSavedPassword = true)
//     · 단축키: 행 선택(ShortcutList_SelectionChanged) → 캡처 상자에 포커스 후 키 입력(CaptureBox_PreviewKeyDown)
//               → 중복이면 확인 후 이동(Assign) ; Clear/Default/Reset all 버튼
//     · 일반: 자동 저장 체크 → AutoSave_Changed ; 선택 외곽선 색/두께 → 미리보기 갱신
//   OK(Ok_Click) → 1) 자동 저장 간격(1~120분 정수) 검증, 틀리면 일반 페이지로 이동 + 경고 후 유지
//                 2) 컨트롤 값을 Settings 사본에 반영 3) 단축키는 기본값과 다른 것만 저장
//                 4) 비밀번호 기억이 꺼져 있으면 ClearSavedPassword = true 5) DialogResult = true
//   Cancel(IsCancel) → 아무 것도 반영되지 않음(사본 폐기).
// ============================================================================
namespace SpatialEditor.App;

/// <summary>설정 대화상자: 로그인 옵션, 단축키 지정, 일반 옵션(자동 저장 등), 선택 외곽선 스타일.</summary>
public partial class SettingsWindow : Window
{
    /// <summary>단축키 목록(ListView) 한 줄. 기능 정보와 현재 지정된 키 조합을 가지며, 키가 바뀌면 화면에 알린다(INotifyPropertyChanged).</summary>
    private sealed class ShortcutRow : INotifyPropertyChanged
    {
        /// <summary>현재 지정된 단축키 문자열(빈 문자열이면 지정 없음).</summary>
        private string gesture = string.Empty;

        /// <summary>이 행이 나타내는 단축키 지정 가능 기능.</summary>
        public required ShortcutCommand Command { get; init; }
        /// <summary>기능 분류(목록 "Category" 열).</summary>
        public string Category => Command.Category;
        /// <summary>기능 이름(목록 "Function" 열).</summary>
        public string Name => Command.Name;
        /// <summary>기능의 기본 단축키(목록 "Default" 열).</summary>
        public string DefaultGesture => Command.DefaultGesture;

        /// <summary>현재 단축키(목록 "Shortcut" 열). 값을 바꾸면 PropertyChanged 로 화면이 갱신된다.</summary>
        public string Gesture
        {
            get => gesture;
            set
            {
                gesture = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gesture)));
            }
        }

        /// <summary>속성 변경 알림 이벤트(데이터 바인딩용).</summary>
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>단축키 목록에 바인딩되는 행 컬렉션.</summary>
    private readonly ObservableCollection<ShortcutRow> rows = new();

    /// <summary>편집 중인 설정 사본. OK 로 닫혔을 때 호출자가 이 값을 채택한다.</summary>
    public AppSettings Settings { get; }

    /// <summary>저장된 비밀번호를 삭제하기로 했거나(삭제 버튼) "비밀번호 기억"을 끈 경우 true. 호출자가 실제 삭제를 수행한다.</summary>
    public bool ClearSavedPassword { get; private set; }

    /// <summary>현재 설정 값으로 모든 페이지의 컨트롤을 초기화한다.</summary>
    /// <param name="settings">현재 앱 설정(사본을 만들어 편집).</param>
    /// <param name="commands">단축키 지정 가능 기능 목록.</param>
    /// <param name="savedLoginId">저장된 로그인 ID(없으면 빈 문자열).</param>
    /// <param name="hasSavedPassword">저장된 비밀번호 존재 여부.</param>
    public SettingsWindow(AppSettings settings, IReadOnlyList<ShortcutCommand> commands, string savedLoginId, bool hasSavedPassword)
    {
        InitializeComponent();
        // 1) 원본을 보호하기 위해 사본을 편집한다. 로그인 페이지: 저장된 ID/비밀번호 상태 문구와 옵션 초기화.
        Settings = settings.Clone();
        SavedLoginText.Text = string.IsNullOrEmpty(savedLoginId)
            ? "No login has been saved yet. Log in once to save the ID."
            : $"Saved ID: {savedLoginId}    Password: {(hasSavedPassword ? "saved (encrypted)" : "not saved")}";
        RememberPasswordCheck.IsChecked = Settings.RememberPassword;
        AutoLoginCheck.IsChecked = Settings.AutoLogin;
        ClearPasswordButton.IsEnabled = hasSavedPassword;
        UpdateLoginOptions();

        // 2) 단축키 페이지: 사용자 지정 값이 있으면 그것을, 없으면 기본 단축키를 현재 값으로 표시한다.
        foreach (var command in commands)
        {
            rows.Add(new ShortcutRow { Command = command, Gesture = Settings.Shortcuts.TryGetValue(command.Id, out var text) ? text : command.DefaultGesture });
        }

        ShortcutList.ItemsSource = rows;
        // 3) 일반 페이지: 자동 저장/자동 버전/주기 값을 채우고 의존 패널의 활성 상태를 맞춘다.
        AutoSaveCheck.IsChecked = Settings.AutoSaveEnabled;
        AutoVersionCheck.IsChecked = Settings.AutoVersionOnSave;
        AutoSaveMinutesBox.Text = Settings.AutoSaveMinutes.ToString();
        UpdateAutoSavePanel();

        // 4) 선택 외곽선 색/두께를 채우고(색 해석 실패 시 Gold), 변경 시 미리보기를 갱신하도록 구독한다.
        SelectionColorEditor.SetColor(System.Windows.Media.ColorConverter.ConvertFromString(Settings.SelectionColor) is System.Windows.Media.Color c
            ? c : System.Windows.Media.Colors.Gold);
        SelectionThicknessSlider.Value = Math.Clamp(Settings.SelectionThickness, 0.5, 8);
        SelectionColorEditor.ColorChanged += (_, _) => UpdateSelectionPreview();
        UpdateSelectionPreview();
    }

    /// <summary>SelectionThicknessSlider 의 ValueChanged 핸들러. 선택 외곽선 미리보기를 다시 그린다.</summary>
    /// <param name="sender">두께 슬라이더.</param>
    /// <param name="e">이전/새 값 인자.</param>
    private void SelectionStyle_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSelectionPreview();

    /// <summary>선택 외곽선 색/두께로 사각형과 꺾은선을 미리보기 캔버스(SelectionPreview)에 다시 그린다.</summary>
    private void UpdateSelectionPreview()
    {
        // XAML 초기화 중에는 컨트롤이 아직 없을 수 있으므로 건너뛴다.
        if (SelectionPreview is null || SelectionThicknessSlider is null || SelectionColorEditor is null || SelectionThicknessText is null)
        {
            return;
        }

        // 두께 표시를 갱신하고 이전 미리보기를 지운 뒤, 같은 선 스타일로 사각형과 꺾은선을 그린다.
        SelectionThicknessText.Text = $"{SelectionThicknessSlider.Value:0.#} px";
        SelectionPreview.Children.Clear();
        var stroke = new System.Windows.Media.SolidColorBrush(SelectionColorEditor.Color);
        var box = new System.Windows.Shapes.Rectangle { Width = 90, Height = 36, Stroke = stroke, StrokeThickness = SelectionThicknessSlider.Value };
        Canvas.SetLeft(box, 20);
        Canvas.SetTop(box, 12);
        var line = new System.Windows.Shapes.Polyline
        {
            Points = new System.Windows.Media.PointCollection { new Point(140, 40), new Point(185, 14), new Point(230, 40), new Point(275, 14) },
            Stroke = stroke,
            StrokeThickness = SelectionThicknessSlider.Value,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
        };
        SelectionPreview.Children.Add(box);
        SelectionPreview.Children.Add(line);
    }

    /// <summary>PageList 의 SelectionChanged 핸들러. 선택 인덱스(0 로그인, 1 단축키, 2 일반)에 맞는 페이지만 보이게 한다.</summary>
    /// <param name="sender">왼쪽 페이지 목록(ListBox).</param>
    /// <param name="e">선택 변경 인자.</param>
    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LoginPage is null)
        {
            return;
        }

        LoginPage.Visibility = PageList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShortcutPage.Visibility = PageList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = PageList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 로그인 페이지 ----

    /// <summary>RememberPasswordCheck 의 Checked/Unchecked 핸들러. 의존 옵션(자동 로그인) 상태를 갱신한다.</summary>
    /// <param name="sender">비밀번호 기억 체크박스.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void LoginOptions_Changed(object sender, RoutedEventArgs e) => UpdateLoginOptions();

    /// <summary>"비밀번호 기억"이 꺼지면 자동 로그인을 해제하고 비활성화한다(저장된 비밀번호가 있어야 자동 로그인이 가능하므로).</summary>
    private void UpdateLoginOptions()
    {
        var remember = RememberPasswordCheck.IsChecked == true;
        AutoLoginCheck.IsEnabled = remember;
        if (!remember)
        {
            AutoLoginCheck.IsChecked = false;
        }
    }

    /// <summary>[Delete the saved password] 버튼 Click 핸들러. OK 를 눌러야 실제 삭제되도록 표시만 해 둔다.</summary>
    /// <param name="sender">삭제 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void ClearPassword_Click(object sender, RoutedEventArgs e)
    {
        ClearSavedPassword = true;
        ClearPasswordButton.IsEnabled = false;
        AutoLoginCheck.IsChecked = false;
        SavedLoginText.Text += "  → will be deleted when you press OK";
    }

    // ---- 단축키 페이지 ----

    /// <summary>단축키 목록에서 선택된 행(없으면 null).</summary>
    private ShortcutRow? SelectedRow => ShortcutList.SelectedItem as ShortcutRow;

    /// <summary>ShortcutList 의 SelectionChanged 핸들러. 선택 안내 문구를 갱신하고 캡처 상자를 초기 상태로 되돌린다.</summary>
    /// <param name="sender">단축키 ListView.</param>
    /// <param name="e">선택 변경 인자.</param>
    private void ShortcutList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShortcutMessage.Text = SelectedRow is { } row ? $"Selected: {row.Name}" : string.Empty;
        CaptureBox.Text = "(press keys)";
    }

    /// <summary>CaptureBox 의 GotKeyboardFocus 핸들러. 키 입력 대기 문구를 표시한다.</summary>
    /// <param name="sender">캡처 상자.</param>
    /// <param name="e">키보드 포커스 변경 인자.</param>
    private void CaptureBox_Focus(object sender, KeyboardFocusChangedEventArgs e) => CaptureBox.Text = "(press keys)";

    /// <summary>CaptureBox 의 PreviewKeyDown 핸들러. 눌린 키 조합을 선택된 기능의 단축키로 지정한다.</summary>
    /// <param name="sender">캡처 상자(읽기 전용 TextBox).</param>
    /// <param name="e">키 입력 인자(Handled 로 처리해 글자가 입력되지 않게 함).</param>
    private void CaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 1) 키 입력이 텍스트 상자에 글자로 들어가지 않도록 먼저 처리 완료로 표시한다.
        e.Handled = true;
        if (SelectedRow is not { } row)
        {
            ShortcutMessage.Text = "Select a function in the list first.";
            return;
        }

        // 2) Alt/IME 가 가로챈 키는 실제 키(SystemKey/ImeProcessedKey)로 복원한다.
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };

        // 3) 수정키(Ctrl/Alt/Shift/Win)만 눌린 상태는 아직 조합이 완성되지 않았으므로 무시한다.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None or Key.DeadCharProcessed)
        {
            return;
        }

        // 4) 현재 수정키와 함께 조합 문자열을 만들어 표시하고 지정한다.
        var gesture = new KeyGesture2(key, Keyboard.Modifiers);
        CaptureBox.Text = gesture.ToString();
        Assign(row, gesture.ToString());
    }

    /// <summary>선택 행에 단축키를 지정한다. 다른 기능이 이미 사용 중이면 확인 후 그 기능의 단축키를 비운다.</summary>
    /// <param name="row">단축키를 지정할 행.</param>
    /// <param name="gesture">지정할 키 조합 문자열(빈 문자열이면 단축키 해제).</param>
    private void Assign(ShortcutRow row, string gesture)
    {
        if (gesture.Length > 0)
        {
            // 같은 단축키를 쓰는 다른 행을 찾아 충돌을 사용자에게 확인한다(대소문자 무시).
            var other = rows.FirstOrDefault(r => r != row && string.Equals(r.Gesture, gesture, StringComparison.OrdinalIgnoreCase));
            if (other is not null)
            {
                var answer = MessageBox.Show(this,
                    $"{gesture} is already assigned to \"{other.Name}\".\nAssign it to \"{row.Name}\" instead?",
                    "Shortcut in use", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    ShortcutMessage.Text = "Not changed.";
                    return;
                }

                other.Gesture = string.Empty;
            }
        }

        // 충돌이 없거나 사용자가 승낙한 경우에만 실제로 지정한다.
        row.Gesture = gesture;
        ShortcutMessage.Text = gesture.Length == 0 ? $"\"{row.Name}\": no shortcut." : $"\"{row.Name}\": {gesture}";
    }

    /// <summary>[Clear] 버튼 Click 핸들러. 선택 기능의 단축키를 비운다.</summary>
    /// <param name="sender">Clear 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void ClearShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
        {
            Assign(row, string.Empty);
        }
    }

    /// <summary>[Default] 버튼 Click 핸들러. 선택 기능의 단축키를 기본값으로 되돌린다.</summary>
    /// <param name="sender">Default 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void DefaultShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
        {
            Assign(row, row.DefaultGesture);
        }
    }

    /// <summary>[Reset all] 버튼 Click 핸들러. 모든 기능의 단축키를 기본값으로 되돌린다(충돌 확인 없이 일괄).</summary>
    /// <param name="sender">Reset all 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void ResetAllShortcuts_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in rows)
        {
            row.Gesture = row.DefaultGesture;
        }

        ShortcutMessage.Text = "All shortcuts were reset to their defaults.";
    }

    // ---- 일반 페이지 ----

    /// <summary>AutoSaveCheck 의 Checked/Unchecked 핸들러. 자동 저장 주기 패널의 활성 상태를 갱신한다.</summary>
    /// <param name="sender">자동 저장 체크박스.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void AutoSave_Changed(object sender, RoutedEventArgs e) => UpdateAutoSavePanel();

    /// <summary>자동 저장이 켜져 있을 때만 주기 입력 패널을 활성화한다.</summary>
    private void UpdateAutoSavePanel()
    {
        if (AutoSavePanel is not null)
        {
            AutoSavePanel.IsEnabled = AutoSaveCheck.IsChecked == true;
        }
    }

    /// <summary>[OK] 버튼 Click 핸들러. 입력을 검증해 설정 사본에 반영하고 true 로 닫는다.</summary>
    /// <param name="sender">OK 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 1) 자동 저장 간격은 1~120 사이 정수여야 한다. 틀리면 해당 입력이 있는 일반 페이지로 이동해 경고한다.
        if (!int.TryParse(AutoSaveMinutesBox.Text.Trim(), out var minutes) || minutes < 1 || minutes > 120)
        {
            PageList.SelectedIndex = 2;
            MessageBox.Show(this, "Enter the auto-save interval as a whole number of minutes between 1 and 120.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2) 컨트롤 값을 설정 사본에 반영한다(자동 로그인은 비밀번호 기억이 켜진 경우에만 유효).
        Settings.RememberPassword = RememberPasswordCheck.IsChecked == true;
        Settings.AutoLogin = Settings.RememberPassword && AutoLoginCheck.IsChecked == true;
        Settings.AutoSaveEnabled = AutoSaveCheck.IsChecked == true;
        Settings.AutoVersionOnSave = AutoVersionCheck.IsChecked == true;
        Settings.AutoSaveMinutes = minutes;
        Settings.SelectionColor = ColorEditor.ToHex(SelectionColorEditor.Color);
        Settings.SelectionThickness = SelectionThicknessSlider.Value;

        // 3) 단축키는 기본값과 다른 항목만 저장한다(나중에 추가된 기본 단축키가 기존 사용자에게도 적용되도록).
        Settings.Shortcuts.Clear();
        foreach (var row in rows)
        {
            if (!string.Equals(row.Gesture, row.DefaultGesture, StringComparison.OrdinalIgnoreCase))
            {
                Settings.Shortcuts[row.Command.Id] = row.Gesture;
            }
        }

        // 4) 비밀번호 기억을 껐다면 저장된 비밀번호를 지우도록 호출자에게 알린다.
        if (!Settings.RememberPassword)
        {
            ClearSavedPassword = true;
        }

        // 5) 확정.
        DialogResult = true;
    }
}
