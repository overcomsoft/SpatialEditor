using System.Windows;
using System.Windows.Media;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.App;

/// <summary>
/// Logs a user in. In setup mode (no users exist yet) the same window creates the first
/// administrator instead.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly UserRepository repository;
    private readonly bool setupMode;

    public AppUser? User { get; private set; }

    public LoginWindow(UserRepository repository, string lastLoginId, bool setupMode)
    {
        InitializeComponent();
        this.repository = repository;
        this.setupMode = setupMode;
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
        }

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

    private void ShowError(string text)
    {
        MessageText.Text = text;
        MessageText.Foreground = Brushes.Firebrick;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        var loginId = LoginIdTextBox.Text.Trim();
        var password = PasswordInput.Password;
        if (loginId.Length == 0 || password.Length == 0)
        {
            ShowError("Enter the ID and password.");
            return;
        }

        if (setupMode && password != ConfirmInput.Password)
        {
            ShowError("The passwords do not match.");
            return;
        }

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
            }

            DialogResult = true;
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
        catch (Exception exception)
        {
            ShowError($"Failed: {exception.Message}");
        }
        finally
        {
            IsEnabled = true;
        }
    }
}
