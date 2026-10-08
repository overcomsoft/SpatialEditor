using System.Windows;
using System.Windows.Controls;
using SpatialEditor.Domain;

namespace SpatialEditor.App;

/// <summary>Creates or edits a role: its name, description and the set of permission keys it grants.</summary>
public partial class RoleEditWindow : Window
{
    private readonly Dictionary<string, CheckBox> permissionChecks = new();

    public string RoleName => NameTextBox.Text.Trim();
    public string? Description => string.IsNullOrWhiteSpace(DescriptionTextBox.Text) ? null : DescriptionTextBox.Text.Trim();
    public IReadOnlyList<string> SelectedPermissions =>
        permissionChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();

    public RoleEditWindow(AppRole? existing)
    {
        InitializeComponent();
        Title = existing is null ? "Add role" : $"Edit role: {existing.Name}";
        foreach (var group in Permissions.All.GroupBy(item => item.Group))
        {
            PermissionsPanel.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 2)
            });
            foreach (var (key, label, _) in group)
            {
                var check = new CheckBox
                {
                    Content = $"{label}  ({key})",
                    IsChecked = existing?.PermissionKeys.Contains(key) == true,
                    Margin = new Thickness(8, 1, 0, 1)
                };
                permissionChecks[key] = check;
                PermissionsPanel.Children.Add(check);
            }
        }

        if (existing is not null)
        {
            NameTextBox.Text = existing.Name;
            DescriptionTextBox.Text = existing.Description ?? string.Empty;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
