using System.Windows;
using System.Windows.Media;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.App;

/// <summary>
/// Asks for the PostgreSQL connection details and only closes with success once a real
/// connection (including the PostGIS check) has worked. The working settings are saved to the
/// settings JSON so the next start can connect automatically.
/// </summary>
public partial class ServerConnectionWindow : Window
{
    private readonly PostgresConnectionSettings current;

    /// <summary>The settings that connected successfully (set when the dialog returns true).</summary>
    public PostgresConnectionSettings? Settings { get; private set; }

    public ServerConnectionWindow(PostgresConnectionSettings initial, string? message = null)
    {
        InitializeComponent();
        current = initial;
        HostTextBox.Text = initial.Host;
        PortTextBox.Text = initial.Port.ToString();
        DatabaseTextBox.Text = initial.Database;
        UsernameTextBox.Text = initial.Username;
        PasswordInput.Password = initial.Password;
        if (message is not null)
        {
            ShowMessage(message, isError: true);
        }
    }

    private void ShowMessage(string text, bool isError)
    {
        MessageText.Text = text;
        MessageText.Foreground = isError ? Brushes.Firebrick : Brushes.SeaGreen;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            ShowMessage("Port must be a number between 1 and 65535.", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(HostTextBox.Text) || string.IsNullOrWhiteSpace(DatabaseTextBox.Text)
            || string.IsNullOrWhiteSpace(UsernameTextBox.Text))
        {
            ShowMessage("Host, database and username are required.", isError: true);
            return;
        }

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

        ConnectButton.IsEnabled = false;
        IsEnabled = false;
        ShowMessage("Connecting...", isError: false);
        try
        {
            await using var repository = new PostGisFeatureRepository(candidate.ConnectionString);
            var info = await repository.TestConnectionAsync();
            await candidate.SaveAsync();
            Settings = candidate;
            ShowMessage(info, isError: false);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            IsEnabled = true;
            ConnectButton.IsEnabled = true;
            ShowMessage($"Connection failed: {exception.Message}", isError: true);
        }
    }
}
