using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpatialEditor.Domain;

namespace SpatialEditor.App;

/// <summary>Adds a user (with an initial password) or edits an existing one (name, active flag, roles).</summary>
public partial class UserEditWindow : Window
{
    private readonly Dictionary<long, CheckBox> roleChecks = new();
    private readonly bool isNew;

    public string LoginId => LoginIdTextBox.Text.Trim();
    public string DisplayName => NameTextBox.Text.Trim();
    public bool UserIsActive => ActiveCheckBox.IsChecked == true;
    public string Password => PasswordInput.Password;
    public bool MustChangePassword => MustChangeCheckBox.IsChecked == true;
    public IReadOnlyList<long> SelectedRoleIds => roleChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();

    public UserEditWindow(IReadOnlyList<AppRole> roles, AppUser? existing)
    {
        InitializeComponent();
        isNew = existing is null;
        Title = isNew ? "Add user" : $"Edit user: {existing!.LoginId}";
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
