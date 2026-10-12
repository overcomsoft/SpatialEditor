using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

// ============================================================================
// MainWindow.Settings.cs : partial class MainWindow 중 "환경설정(단축키 / 자동 저장 / 설정 창)" 담당 부분
// ----------------------------------------------------------------------------
// 흐름도
//  [시작] InitializeSettings: AppSettings.Load → BuildShortcutCommands(전체 명령 등록)
//     → ApplyShortcuts(사용자 재정의 반영 + 메뉴 항목 InputGestureText 표시) → 자동 저장 타이머 Tick 연결 → ApplyAutoSave
//  [키 입력] HandleShortcut(KeyEventArgs)
//     텍스트 입력 중이면 무시 → 한글 IME/시스템 키 보정 → 보조키 단독 입력 무시
//     → 등록 명령을 순회: KeyGesture2.TryParse 로 키 일치 확인 → 보조키 비교(ShiftReverses 면 Shift 무시)
//     → CanExecute 통과 시 e.Handled=true → 반복 입력(IsRepeat)이 아니면 Run(reverse)
//        (reverse = Shift XOR CapsLock 토글, 예: 반시계 회전)
//  [자동 저장] ApplyAutoSave: 타이머 정지 → 설정이 켜져 있으면 간격(1~120분)으로 시작
//     → 타이머 Tick → AutoSaveTickAsync: 중복 실행/비편집/미로그인/변경 없음/드래그 중/바쁨 표시/열린 대화상자가 있으면 건너뜀
//     → SaveEditsAsync(auto: true) → 예외는 상태줄에 표시 → finally 로 플래그 해제
//  [설정 창] OpenSettings_Click: SettingsWindow(설정, 명령 목록, 저장된 ID/암호 여부) → 확인 시
//     AppSettings.Save → (요청 시) 저장된 암호 삭제 → ApplyShortcuts/ApplyAutoSave
//     → 편집 모드면 다시 그리기(선택 색/두께 반영), 보기 모드면 선택 피처 다시 강조 → 상태줄 안내
// ============================================================================
namespace SpatialEditor.App;

/// <summary>MainWindow 의 환경설정 부분: 설정 가능한 단축키, 자동 저장, 설정 대화상자.</summary>
public partial class MainWindow
{
    /// <summary>현재 적용 중인 사용자 설정(시작 시 파일에서 로드, 설정 창에서 확인하면 교체).</summary>
    private AppSettings appSettings = new();
    /// <summary>단축키를 바인딩할 수 있는 모든 명령(BuildShortcutCommands 가 채움). 설정 창과 키 처리에서 사용.</summary>
    private readonly List<ShortcutCommand> shortcutCommands = new();
    /// <summary>자동 저장 주기 타이머(간격은 ApplyAutoSave 가 설정).</summary>
    private readonly DispatcherTimer autoSaveTimer = new();
    /// <summary>자동 저장이 진행 중인지 여부(겹쳐 실행되는 것을 방지).</summary>
    private bool autoSaving;

    /// <summary>설정을 불러와 단축키 명령을 등록·적용하고 자동 저장 타이머를 준비합니다(창 초기화 시 1회 호출).</summary>
    private void InitializeSettings()
    {
        appSettings = AppSettings.Load();
        BuildShortcutCommands();
        ApplyShortcuts();
        autoSaveTimer.Tick += async (_, _) => await AutoSaveTickAsync();
        ApplyAutoSave();
    }

    // ---------------------------------------------------------------- shortcuts

    /// <summary>
    /// 단축키를 연결할 수 있는 모든 명령을 등록합니다(id, 분류, 이름, 기본 키, 실행 동작, 실행 가능 조건, 메뉴 머리글).
    /// 대부분은 기존 메뉴/버튼 클릭 핸들러를 그대로 호출합니다.
    /// </summary>
    private void BuildShortcutCommands()
    {
        // 지역 함수 Add: 인수 없는 동작(Action)을 ShortcutCommand 로 감싸 목록에 등록합니다(역방향 인수는 무시).
        void Add(string id, string category, string name, string gesture, Action run, Func<bool>? can = null, string? menu = null) =>
            shortcutCommands.Add(new ShortcutCommand(id, category, name, gesture, _ => run(), can, menu));

        // 지역 함수 Click: 라우트 이벤트 핸들러를 빈 인수로 직접 호출해, 메뉴/버튼 클릭과 같은 동작을 실행합니다.
        void Click(RoutedEventHandler handler) => handler(this, new RoutedEventArgs());

        // 파일 메뉴 명령
        Add("file.connect", "File", "Connect to server...", "", () => Click(ConnectServer_Click), null, "Connect to _server...");
        Add("file.import", "File", "Import DXF", "", () => Click(ImportDxf_Click), () => ImportButton.IsEnabled, "_Import DXF");
        Add("file.export", "File", "Export DXF (blocks)", "", () => Click(ExportDxf_Click), null, "E_xport DXF (blocks)...");
        Add("file.cancelImport", "File", "Cancel import", "", () => Click(CancelImport_Click), () => CancelImportButton.IsEnabled, "_Cancel import");
        Add("file.deleteImport", "File", "Delete imported DXF", "", () => Click(DeleteImportedDxf_Click), () => DeleteImportButton.IsEnabled, "_Delete imported DXF");
        Add("file.exit", "File", "Exit", "", () => Click(ExitApp_Click), null, "E_xit");

        // 편집 및 버전 명령
        Add("edit.mode", "Edit", "Edit mode on/off", "", () => Click(ToggleEditMode_Click), () => EditModeButton.IsEnabled, "_Edit mode");
        Add("edit.save", "Edit", "Save changes", "Ctrl+S", () => Click(SaveEdits_Click), () => isEditMode && SaveEditsButton.IsEnabled, "_Save changes");
        Add("edit.discard", "Edit", "Discard changes", "", () => Click(DiscardEdits_Click), () => DiscardEditsButton.IsEnabled, "_Discard changes");
        Add("edit.cancelEdits", "Edit", "Cancel edits (restore saved state)", "", CancelEdits, () => isEditMode, "Cancel _edits");
        Add("edit.undo", "Edit", "Undo", "Ctrl+Z", UndoEdit, () => isEditMode && undoStack.Count > 0, "_Undo");
        Add("edit.redo", "Edit", "Redo", "Ctrl+R", RedoEdit, () => isEditMode && redoStack.Count > 0, "_Redo");
        Add("edit.selectAll", "Edit", "Select all objects", "Ctrl+A", SelectAllObjects, () => isEditMode, "Select _all");
        Add("version.save", "Version", "Save version...", "Ctrl+Shift+S", () => Click(SaveVersion_Click), () => VersionSaveButton.IsEnabled, "_Save version…");
        Add("version.manager", "Version", "Version manager...", "", () => Click(VersionManager_Click), () => VersionManagerButton.IsEnabled, "Version _manager…");
        Add("version.layouts", "Version", "Drawings...", "", () => Click(LayoutManager_Click), () => LayoutButton.IsEnabled, "_Drawings…");
        Add("edit.duplicate", "Edit", "Duplicate", "Ctrl+D", () => Click(DuplicateSelected_Click), () => isEditMode && DuplicateButton.IsEnabled, "D_uplicate");
        Add("edit.delete", "Edit", "Delete object", "Delete", () => Click(DeleteSelected_Click), () => isEditMode && DeleteObjectButton.IsEnabled, "De_lete object");
        Add("edit.group", "Edit", "Group into block", "Ctrl+G", () => Click(GroupSelected_Click), () => isEditMode && GroupButton.IsEnabled, "_Group into block");
        Add("edit.vertex", "Edit", "Vertex edit", "", () => Click(ToggleVertexEdit_Click), () => VertexEditButton.IsEnabled, "_Vertex edit");
        Add("edit.deselect", "Edit", "Deselect all", "", () => Click(DeselectAll_Click));
        // 회전은 Shift(또는 Caps Lock)가 반시계 방향을 뜻하므로 shiftReverses 를 켭니다.
        shortcutCommands.Add(new ShortcutCommand("edit.rotate", "Edit", "Rotate selected (Shift: counter-clockwise)", "R",
            reverse => RotateSelected(clockwise: !reverse),
            () => isEditMode && selectedEditIds.Count > 0, shiftReverses: true));

        // 보기 명령
        Add("view.zoomIn", "View", "Zoom in", "", () => Click(ZoomIn_Click), null, "Zoom _in");
        Add("view.zoomOut", "View", "Zoom out", "", () => Click(ZoomOut_Click), null, "Zoom _out");
        Add("view.fit", "View", "Zoom to full extent", "F", () => Click(FitView_Click), null, "Zoom to _full extent");
        Add("view.reset", "View", "Reset view", "", () => Click(ResetView_Click), null, "_Reset view");

        // 레이어 명령
        Add("layer.load", "Layers", "Load layers...", "", () => Click(LoadLayers_Click), () => isConnected, "_Load layers...");
        Add("layer.refresh", "Layers", "Refresh layers", "", () => Click(RefreshLayers_Click), () => isConnected, "_Refresh layers");
        Add("layer.style", "Layers", "Layer style...", "", () => Click(SelectedLayerStyle_Click), null, "Layer _style...");
        Add("layer.zoom", "Layers", "Zoom to selected layer", "", () => Click(SelectedLayerZoom_Click), null, "_Zoom to selected layer");
        Add("layer.edit", "Layers", "Edit selected layer", "", () => Click(SelectedLayerEdit_Click), () => EditModeButton.IsEnabled, "_Edit selected layer");
        Add("layer.showAll", "Layers", "Show all layers", "", () => Click(ShowAllLayers_Click), null, "Show _all layers");
        Add("layer.hideAll", "Layers", "Hide all layers", "", () => Click(HideAllLayers_Click), null, "_Hide all layers");

        // 도구 / 관리 / 도움말 명령
        Add("tools.validate", "Tools", "Validate geometry", "", () => Click(Validate_Click), null, "_Validate geometry");
        Add("tools.addPoint", "Tools", "Add point", "", () => Click(AddPoint_Click), null, "Add _point");
        Add("tools.library", "Tools", "Block library...", "", () => Click(OpenLibrary_Click), () => LibraryMenuItem.IsEnabled, "Block _library...");
        Add("tools.registerLibrary", "Tools", "Register selected block in library...", "", () => Click(RegisterInLibrary_Click), () => RegisterLibraryMenuItem.IsEnabled, "_Register selected block in library...");
        Add("tools.copyScreen", "Tools", "Copy screen to clipboard", "Ctrl+P", () => Click(CopyScreen_Click), null, "_Copy screen to clipboard");
        Add("tools.settings", "Tools", "Settings...", "Ctrl+OemComma", () => Click(OpenSettings_Click), null, "_Settings...");
        Add("admin.users", "Admin", "User management...", "", () => Click(ManageUsers_Click), () => UsersMenuItem.IsEnabled, "_User management...");
        Add("admin.password", "Admin", "Change my password...", "", () => Click(ChangePassword_Click), () => PasswordMenuItem.IsEnabled, "Change my _password...");
        Add("admin.logout", "Admin", "Log in / Log out", "", () => Click(Logout_Click), () => LogoutMenuItem.IsEnabled, "_Log in|_Log out");
        Add("help.manual", "Help", "User manual (PDF)", "F1", () => Click(UserManual_Click), null, "User _manual (PDF)");
        Add("help.about", "Help", "About OmniDT SE", "", () => Click(About_Click), null, "_About OmniDT SE");
    }

    /// <summary>저장된 재정의를 모든 명령에 적용하고, 결과 단축키를 해당 메뉴 항목 옆(InputGestureText)에 표시합니다.</summary>
    private void ApplyShortcuts()
    {
        // 1) 사용자 재정의가 있으면 그 값을, 없으면 기본 단축키를 현재 제스처로 삼습니다.
        foreach (var command in shortcutCommands)
        {
            command.Gesture = appSettings.Shortcuts.TryGetValue(command.Id, out var text) ? text : command.DefaultGesture;
        }

        // 2) 메뉴 머리글 → 표시용 단축키 문구 사전을 만듭니다(Oem 키 이름은 기호로 바꿔 보기 좋게).
        var byHeader = new Dictionary<string, string>();
        foreach (var command in shortcutCommands.Where(c => c.MenuHeader is not null))
        {
            foreach (var header in command.MenuHeader!.Split('|'))
            {
                byHeader[header] = command.Gesture.Replace("OemComma", ",").Replace("OemPlus", "+").Replace("OemMinus", "-");
            }
        }

        // 지역 함수 Walk: 메뉴 트리를 재귀로 돌며 머리글이 일치하는 항목에 단축키 문구를 표시합니다.
        void Walk(ItemCollection items)
        {
            foreach (var item in items.OfType<MenuItem>())
            {
                if (item.Header is string header && byHeader.TryGetValue(header, out var gesture))
                {
                    item.InputGestureText = gesture;
                }

                Walk(item.Items);
            }
        }

        Walk(MainMenu.Items);
    }

    /// <summary>눌린 키에 연결된 명령이 있으면 실행합니다. 텍스트 상자에 입력 중일 때는 가로채지 않습니다.</summary>
    /// <param name="e">키 이벤트 인수. 처리하면 <c>Handled</c> 가 true 로 설정됩니다.</param>
    private void HandleShortcut(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or PasswordBox)
        {
            return;
        }

        // 한글 IME 는 글자 키를 ImeProcessed 로 보고하며 실제 키는 ImeProcessedKey 에 있습니다(Alt 조합은 System/SystemKey).
        var key = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key
        };

        // 보조키만 눌린 경우(또는 IME 중간 입력)는 단축키가 아니므로 무시합니다.
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.DeadCharProcessed)
        {
            return;
        }

        // 등록된 명령을 순회하며 키와 보조키가 정확히 일치하고 실행 가능한 첫 명령을 실행합니다.
        var modifiers = Keyboard.Modifiers;
        foreach (var command in shortcutCommands)
        {
            if (!KeyGesture2.TryParse(command.Gesture, out var gesture) || gesture.Key != key)
            {
                continue;
            }

            // ShiftReverses 명령은 Shift 가 방향 선택용이므로 비교에서 Shift 를 제외합니다.
            var pressed = modifiers;
            var wanted = gesture.Modifiers;
            if (command.ShiftReverses)
            {
                pressed &= ~ModifierKeys.Shift;
                wanted &= ~ModifierKeys.Shift;
            }

            if (pressed != wanted || !command.CanExecute())
            {
                continue;
            }

            e.Handled = true;
            if (!e.IsRepeat)
            {
                // Shift(또는 Caps Lock)는 반대 방향(예: 반시계 회전)을 요청합니다. 둘이 모두 켜지면 서로 상쇄(XOR)됩니다.
                var reverse = command.ShiftReverses
                    && (((modifiers & ModifierKeys.Shift) != 0) ^ Keyboard.IsKeyToggled(Key.CapsLock));
                command.Run(reverse);
            }

            return;
        }
    }

    // ---------------------------------------------------------------- auto-save

    /// <summary>자동 저장 타이머를 설정에 맞게 다시 시작합니다(꺼져 있으면 정지만). 간격은 1~120분으로 제한.</summary>
    private void ApplyAutoSave()
    {
        autoSaveTimer.Stop();
        if (appSettings.AutoSaveEnabled)
        {
            autoSaveTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(appSettings.AutoSaveMinutes, 1, 120));
            autoSaveTimer.Start();
        }
    }

    /// <summary>
    /// 자동 저장 타이머 Tick 처리. 이미 저장 중이거나, 편집 모드가 아니거나, 로그인 전이거나, 변경이 없거나,
    /// 드래그 중이거나, 바쁨 표시/열린 대화상자가 있으면 사용자를 방해하지 않도록 건너뜁니다.
    /// </summary>
    private async Task AutoSaveTickAsync()
    {
        if (autoSaving || !isEditMode || currentUser is null || !HasPendingEdits()
            || editDragKind != EditDragKind.None || BusyOverlay.Visibility == Visibility.Visible
            || OwnedWindows.OfType<Window>().Any(window => window.IsVisible))
        {
            return;
        }

        autoSaving = true;
        try
        {
            await SaveEditsAsync(auto: true);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Auto-save failed: {exception.Message}";
        }
        finally
        {
            autoSaving = false;
        }
    }

    // ---------------------------------------------------------------- dialog

    /// <summary>설정 대화상자를 열고, 확인하면 설정을 저장·적용합니다(저장된 암호 삭제 요청 포함).</summary>
    /// <param name="sender">이벤트를 일으킨 메뉴 항목.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private async void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(appSettings, shortcutCommands, connectionSettings.LastLoginId,
            connectionSettings.LastLoginPassword.Length > 0) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 1) 설정 창의 결과로 현재 설정을 교체하고 파일에 저장합니다(저장 실패는 경고만 하고 계속).
        appSettings = dialog.Settings;
        try
        {
            appSettings.Save();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not save the settings file: {exception.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 2) 사용자가 요청했으면 연결 설정 파일에 저장된 암호를 지웁니다.
        if (dialog.ClearSavedPassword && connectionSettings.LastLoginPassword.Length > 0)
        {
            connectionSettings.LastLoginPassword = string.Empty;
            try
            {
                await connectionSettings.SaveAsync();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"Could not delete the saved password: {exception.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // 3) 새 설정을 즉시 반영: 단축키/자동 저장, 그리고 선택 표시(색·두께) 다시 그리기.
        ApplyShortcuts();
        ApplyAutoSave();
        if (isEditMode)
        {
            RenderEditableLayerObjects();
        }
        else if (selectedFeature is not null)
        {
            var feature = selectedFeature;
            var layer = selectedFeatureLayer;
            HighlightFeature(feature);
            selectedFeature = feature;
            selectedFeatureLayer = layer;
        }

        StatusText.Text = "Settings saved.";
    }
}
