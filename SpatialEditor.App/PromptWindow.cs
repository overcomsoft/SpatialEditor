using System.Windows;
using System.Windows.Controls;

namespace SpatialEditor.App;

/// <summary>Minimal single-line text prompt.</summary>
internal static class PromptWindow
{
    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(3) };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(20, 4, 20, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(20, 4, 20, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = $"OmniDT SE - {title}",
            Content = panel,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.WhiteSmoke
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text : null;
    }
}
