using System.Windows;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.App;

/// <summary>
/// Administrators add, edit, deactivate and delete users, reset passwords, unlock accounts and
/// define roles with their permissions; the audit tab lists recent security-relevant events.
/// </summary>
public partial class UserManagementWindow : Window
{
    private sealed record UserRow(AppUser User)
    {
        public string LoginId => User.LoginId;
        public string DisplayName => User.DisplayName;
        public string Roles => string.Join(", ", User.Roles);
        public string Active => User.IsActive ? "Yes" : "No";
        public string Status => User.LockedUntil is { } until && until > DateTime.UtcNow
            ? $"Locked until {until.ToLocalTime():HH:mm}"
            : User.MustChangePassword ? "Must change password" : string.Empty;
        public string LastLogin => User.LastLoginAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
    }

    private sealed record RoleRow(AppRole Role)
    {
        public string Name => Role.Name;
        public string Description => Role.Description ?? string.Empty;
        public string BuiltIn => Role.IsSystem ? "Yes" : string.Empty;
        public int UserCount => Role.UserCount;
        public int PermissionCount => Role.PermissionKeys.Count;
    }

    private sealed record AuditRow(AuditEntry Entry)
    {
        public string Time => Entry.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string User => Entry.UserName ?? "-";
        public string Action => Entry.Action;
        public string Target => string.IsNullOrEmpty(Entry.EntityType) ? string.Empty : $"{Entry.EntityType} {Entry.EntityId}";
        public string Detail => Entry.Summary ?? string.Empty;
    }

    private readonly UserRepository repository;
    private readonly AppUser actor;
    private IReadOnlyList<AppRole> roles = Array.Empty<AppRole>();

    public UserManagementWindow(UserRepository repository, AppUser actor)
    {
        InitializeComponent();
        this.repository = repository;
        this.actor = actor;
        AuditTab.Visibility = actor.Has(Permissions.AuditView) ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            roles = await repository.ListRolesAsync();
            RolesGrid.ItemsSource = roles.Select(role => new RoleRow(role)).ToList();
            UsersGrid.ItemsSource = (await repository.ListUsersAsync()).Select(user => new UserRow(user)).ToList();
            if (actor.Has(Permissions.AuditView))
            {
                AuditGrid.ItemsSource = (await repository.ListAuditAsync()).Select(entry => new AuditRow(entry)).ToList();
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ShowError(Exception exception) =>
        MessageBox.Show(this, exception.Message, "User management", MessageBoxButton.OK,
            exception is InvalidOperationException ? MessageBoxImage.Warning : MessageBoxImage.Error);

    private AppUser? SelectedUser => (UsersGrid.SelectedItem as UserRow)?.User;
    private AppRole? SelectedRole => (RolesGrid.SelectedItem as RoleRow)?.Role;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    // ------------------------------------------------------------- users

    private async void AddUser_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new UserEditWindow(roles, null) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await repository.CreateUserAsync(actor, dialog.LoginId, dialog.DisplayName, dialog.Password,
                dialog.MustChangePassword, dialog.SelectedRoleIds);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    private async void EditUser_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUser is not { } user)
        {
            return;
        }

        var dialog = new UserEditWindow(roles, user) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await repository.UpdateUserAsync(actor, user.Id, dialog.DisplayName, dialog.UserIsActive, dialog.SelectedRoleIds);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUser is not { } user)
        {
            return;
        }

        var dialog = new PasswordWindow("Reset password", $"Set a new password for '{user.LoginId}'.", askOld: false, offerMustChange: true)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await repository.ResetPasswordAsync(actor, user.Id, dialog.NewPassword, dialog.MustChange);
            MessageBox.Show(this, $"The password of '{user.LoginId}' was reset.", "User management");
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    private async void UnlockUser_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUser is not { } user)
        {
            return;
        }

        try
        {
            await repository.UnlockUserAsync(actor, user.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    private async void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUser is not { } user)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Permanently delete user '{user.LoginId}'?\n\nUsers with activity history cannot be deleted; deactivate them instead (Edit).",
            "Delete user", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await repository.DeleteUserAsync(actor, user.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    // ------------------------------------------------------------- roles

    private async void AddRole_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RoleEditWindow(null) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await repository.CreateRoleAsync(actor, dialog.RoleName, dialog.Description, dialog.SelectedPermissions);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    private async void EditRole_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRole is not { } role)
        {
            return;
        }

        var dialog = new RoleEditWindow(role) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await repository.UpdateRoleAsync(actor, role.Id, dialog.RoleName, dialog.Description, dialog.SelectedPermissions);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }

    private async void DeleteRole_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRole is not { } role)
        {
            return;
        }

        var answer = MessageBox.Show(this, $"Delete the role '{role.Name}'?", "Delete role",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await repository.DeleteRoleAsync(actor, role.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        await ReloadAsync();
    }
}
