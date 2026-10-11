using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SpatialEditor.App;

/// <summary>Settings dialog: login options, shortcut assignment and general options (auto-save).</summary>
public partial class SettingsWindow : Window
{
    private sealed class ShortcutRow : INotifyPropertyChanged
    {
        private string gesture = string.Empty;

        public required ShortcutCommand Command { get; init; }
        public string Category => Command.Category;
        public string Name => Command.Name;
        public string DefaultGesture => Command.DefaultGesture;

        public string Gesture
        {
            get => gesture;
            set
            {
                gesture = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gesture)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<ShortcutRow> rows = new();

    public AppSettings Settings { get; }

    /// <summary>True when the user chose to delete the saved password (or turned "remember" off).</summary>
    public bool ClearSavedPassword { get; private set; }

    public SettingsWindow(AppSettings settings, IReadOnlyList<ShortcutCommand> commands, string savedLoginId, bool hasSavedPassword)
    {
        InitializeComponent();
        Settings = settings.Clone();
        SavedLoginText.Text = string.IsNullOrEmpty(savedLoginId)
            ? "No login has been saved yet. Log in once to save the ID."
            : $"Saved ID: {savedLoginId}    Password: {(hasSavedPassword ? "saved (encrypted)" : "not saved")}";
        RememberPasswordCheck.IsChecked = Settings.RememberPassword;
        AutoLoginCheck.IsChecked = Settings.AutoLogin;
        ClearPasswordButton.IsEnabled = hasSavedPassword;
        UpdateLoginOptions();

        foreach (var command in commands)
        {
            rows.Add(new ShortcutRow { Command = command, Gesture = Settings.Shortcuts.TryGetValue(command.Id, out var text) ? text : command.DefaultGesture });
        }

        ShortcutList.ItemsSource = rows;
        AutoSaveCheck.IsChecked = Settings.AutoSaveEnabled;
        AutoSaveMinutesBox.Text = Settings.AutoSaveMinutes.ToString();
        UpdateAutoSavePanel();

        SelectionColorEditor.SetColor(System.Windows.Media.ColorConverter.ConvertFromString(Settings.SelectionColor) is System.Windows.Media.Color c
            ? c : System.Windows.Media.Colors.Gold);
        SelectionThicknessSlider.Value = Math.Clamp(Settings.SelectionThickness, 0.5, 8);
        SelectionColorEditor.ColorChanged += (_, _) => UpdateSelectionPreview();
        UpdateSelectionPreview();
    }

    private void SelectionStyle_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSelectionPreview();

    private void UpdateSelectionPreview()
    {
        if (SelectionPreview is null || SelectionThicknessSlider is null || SelectionColorEditor is null || SelectionThicknessText is null)
        {
            return;
        }

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

    // ---- Login ----

    private void LoginOptions_Changed(object sender, RoutedEventArgs e) => UpdateLoginOptions();

    private void UpdateLoginOptions()
    {
        var remember = RememberPasswordCheck.IsChecked == true;
        AutoLoginCheck.IsEnabled = remember;
        if (!remember)
        {
            AutoLoginCheck.IsChecked = false;
        }
    }

    private void ClearPassword_Click(object sender, RoutedEventArgs e)
    {
        ClearSavedPassword = true;
        ClearPasswordButton.IsEnabled = false;
        AutoLoginCheck.IsChecked = false;
        SavedLoginText.Text += "  → will be deleted when you press OK";
    }

    // ---- Shortcuts ----

    private ShortcutRow? SelectedRow => ShortcutList.SelectedItem as ShortcutRow;

    private void ShortcutList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShortcutMessage.Text = SelectedRow is { } row ? $"Selected: {row.Name}" : string.Empty;
        CaptureBox.Text = "(press keys)";
    }

    private void CaptureBox_Focus(object sender, KeyboardFocusChangedEventArgs e) => CaptureBox.Text = "(press keys)";

    private void CaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (SelectedRow is not { } row)
        {
            ShortcutMessage.Text = "Select a function in the list first.";
            return;
        }

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None or Key.DeadCharProcessed)
        {
            return;
        }

        var gesture = new KeyGesture2(key, Keyboard.Modifiers);
        CaptureBox.Text = gesture.ToString();
        Assign(row, gesture.ToString());
    }

    private void Assign(ShortcutRow row, string gesture)
    {
        if (gesture.Length > 0)
        {
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

        row.Gesture = gesture;
        ShortcutMessage.Text = gesture.Length == 0 ? $"\"{row.Name}\": no shortcut." : $"\"{row.Name}\": {gesture}";
    }

    private void ClearShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
        {
            Assign(row, string.Empty);
        }
    }

    private void DefaultShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
        {
            Assign(row, row.DefaultGesture);
        }
    }

    private void ResetAllShortcuts_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in rows)
        {
            row.Gesture = row.DefaultGesture;
        }

        ShortcutMessage.Text = "All shortcuts were reset to their defaults.";
    }

    // ---- General ----

    private void AutoSave_Changed(object sender, RoutedEventArgs e) => UpdateAutoSavePanel();

    private void UpdateAutoSavePanel()
    {
        if (AutoSavePanel is not null)
        {
            AutoSavePanel.IsEnabled = AutoSaveCheck.IsChecked == true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AutoSaveMinutesBox.Text.Trim(), out var minutes) || minutes < 1 || minutes > 120)
        {
            PageList.SelectedIndex = 2;
            MessageBox.Show(this, "Enter the auto-save interval as a whole number of minutes between 1 and 120.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Settings.RememberPassword = RememberPasswordCheck.IsChecked == true;
        Settings.AutoLogin = Settings.RememberPassword && AutoLoginCheck.IsChecked == true;
        Settings.AutoSaveEnabled = AutoSaveCheck.IsChecked == true;
        Settings.AutoSaveMinutes = minutes;
        Settings.SelectionColor = ColorEditor.ToHex(SelectionColorEditor.Color);
        Settings.SelectionThickness = SelectionThicknessSlider.Value;

        // Store only the differences from the defaults so new default shortcuts reach existing users.
        Settings.Shortcuts.Clear();
        foreach (var row in rows)
        {
            if (!string.Equals(row.Gesture, row.DefaultGesture, StringComparison.OrdinalIgnoreCase))
            {
                Settings.Shortcuts[row.Command.Id] = row.Gesture;
            }
        }

        if (!Settings.RememberPassword)
        {
            ClearSavedPassword = true;
        }

        DialogResult = true;
    }
}
