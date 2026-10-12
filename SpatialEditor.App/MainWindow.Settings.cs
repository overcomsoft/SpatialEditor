using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SpatialEditor.App;

/// <summary>Preferences: configurable shortcuts, auto-save, and the Settings dialog.</summary>
public partial class MainWindow
{
    private AppSettings appSettings = new();
    private readonly List<ShortcutCommand> shortcutCommands = new();
    private readonly DispatcherTimer autoSaveTimer = new();
    private bool autoSaving;

    private void InitializeSettings()
    {
        appSettings = AppSettings.Load();
        BuildShortcutCommands();
        ApplyShortcuts();
        autoSaveTimer.Tick += async (_, _) => await AutoSaveTickAsync();
        ApplyAutoSave();
    }

    // ---------------------------------------------------------------- shortcuts

    private void BuildShortcutCommands()
    {
        void Add(string id, string category, string name, string gesture, Action run, Func<bool>? can = null, string? menu = null) =>
            shortcutCommands.Add(new ShortcutCommand(id, category, name, gesture, _ => run(), can, menu));

        void Click(RoutedEventHandler handler) => handler(this, new RoutedEventArgs());

        // File
        Add("file.connect", "File", "Connect to server...", "", () => Click(ConnectServer_Click), null, "Connect to _server...");
        Add("file.import", "File", "Import DXF", "", () => Click(ImportDxf_Click), () => ImportButton.IsEnabled, "_Import DXF");
        Add("file.export", "File", "Export DXF (blocks)", "", () => Click(ExportDxf_Click), null, "E_xport DXF (blocks)...");
        Add("file.cancelImport", "File", "Cancel import", "", () => Click(CancelImport_Click), () => CancelImportButton.IsEnabled, "_Cancel import");
        Add("file.deleteImport", "File", "Delete imported DXF", "", () => Click(DeleteImportedDxf_Click), () => DeleteImportButton.IsEnabled, "_Delete imported DXF");
        Add("file.exit", "File", "Exit", "", () => Click(ExitApp_Click), null, "E_xit");

        // Edit
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
        shortcutCommands.Add(new ShortcutCommand("edit.rotate", "Edit", "Rotate selected (Shift: counter-clockwise)", "R",
            reverse => RotateSelected(clockwise: !reverse),
            () => isEditMode && selectedEditIds.Count > 0, shiftReverses: true));

        // View
        Add("view.zoomIn", "View", "Zoom in", "", () => Click(ZoomIn_Click), null, "Zoom _in");
        Add("view.zoomOut", "View", "Zoom out", "", () => Click(ZoomOut_Click), null, "Zoom _out");
        Add("view.fit", "View", "Zoom to full extent", "F", () => Click(FitView_Click), null, "Zoom to _full extent");
        Add("view.reset", "View", "Reset view", "", () => Click(ResetView_Click), null, "_Reset view");

        // Layers
        Add("layer.load", "Layers", "Load layers...", "", () => Click(LoadLayers_Click), () => isConnected, "_Load layers...");
        Add("layer.refresh", "Layers", "Refresh layers", "", () => Click(RefreshLayers_Click), () => isConnected, "_Refresh layers");
        Add("layer.style", "Layers", "Layer style...", "", () => Click(SelectedLayerStyle_Click), null, "Layer _style...");
        Add("layer.zoom", "Layers", "Zoom to selected layer", "", () => Click(SelectedLayerZoom_Click), null, "_Zoom to selected layer");
        Add("layer.edit", "Layers", "Edit selected layer", "", () => Click(SelectedLayerEdit_Click), () => EditModeButton.IsEnabled, "_Edit selected layer");
        Add("layer.showAll", "Layers", "Show all layers", "", () => Click(ShowAllLayers_Click), null, "Show _all layers");
        Add("layer.hideAll", "Layers", "Hide all layers", "", () => Click(HideAllLayers_Click), null, "_Hide all layers");

        // Tools / Admin / Help
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

    /// <summary>Applies the saved overrides to every command and shows the result next to the menu items.</summary>
    private void ApplyShortcuts()
    {
        foreach (var command in shortcutCommands)
        {
            command.Gesture = appSettings.Shortcuts.TryGetValue(command.Id, out var text) ? text : command.DefaultGesture;
        }

        var byHeader = new Dictionary<string, string>();
        foreach (var command in shortcutCommands.Where(c => c.MenuHeader is not null))
        {
            foreach (var header in command.MenuHeader!.Split('|'))
            {
                byHeader[header] = command.Gesture.Replace("OemComma", ",").Replace("OemPlus", "+").Replace("OemMinus", "-");
            }
        }

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

    /// <summary>Runs the command bound to the pressed key, if any. Typing in a text box is never intercepted.</summary>
    private void HandleShortcut(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or PasswordBox)
        {
            return;
        }

        // The Korean IME reports letter keys as ImeProcessed; the real key is in ImeProcessedKey.
        var key = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key
        };

        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.DeadCharProcessed)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        foreach (var command in shortcutCommands)
        {
            if (!KeyGesture2.TryParse(command.Gesture, out var gesture) || gesture.Key != key)
            {
                continue;
            }

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
                // Shift (or Caps Lock) asks for the reverse direction, e.g. counter-clockwise rotation.
                var reverse = command.ShiftReverses
                    && (((modifiers & ModifierKeys.Shift) != 0) ^ Keyboard.IsKeyToggled(Key.CapsLock));
                command.Run(reverse);
            }

            return;
        }
    }

    // ---------------------------------------------------------------- auto-save

    private void ApplyAutoSave()
    {
        autoSaveTimer.Stop();
        if (appSettings.AutoSaveEnabled)
        {
            autoSaveTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(appSettings.AutoSaveMinutes, 1, 120));
            autoSaveTimer.Start();
        }
    }

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

    private async void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(appSettings, shortcutCommands, connectionSettings.LastLoginId,
            connectionSettings.LastLoginPassword.Length > 0) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        appSettings = dialog.Settings;
        try
        {
            appSettings.Save();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not save the settings file: {exception.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

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
