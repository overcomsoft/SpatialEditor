using System.Windows;
using System.Windows.Media;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.App;

/// <summary>Asks for a new password: own change (asks the current one) or an administrator reset.</summary>
public partial class PasswordWindow : Window
{
    public string OldPassword => OldInput.Password;
    public string NewPassword => NewInput.Password;
    public bool MustChange => MustChangeCheckBox.IsChecked == true;

    /// <param name="askOld">Ask for the current password (changing your own).</param>
    /// <param name="allowCancel">False when a change is forced at login.</param>
    /// <param name="offerMustChange">Show the "require change at next login" option (admin reset).</param>
    public PasswordWindow(string title, string message, bool askOld, bool allowCancel = true, bool offerMustChange = false)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = $"{message} (at least {PasswordHasher.MinimumLength} characters)";
        OldLabel.Visibility = OldInput.Visibility = askOld ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
        MustChangeCheckBox.Visibility = offerMustChange ? Visibility.Visible : Visibility.Collapsed;
        MustChangeCheckBox.IsChecked = offerMustChange;
        if (!askOld)
        {
            Height = 290;
        }

        Loaded += (_, _) => (askOld ? OldInput : NewInput).Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordHasher.ValidatePolicy(NewInput.Password) is { } problem)
        {
            MessageText.Text = problem;
            MessageText.Foreground = Brushes.Firebrick;
            return;
        }

        if (NewInput.Password != ConfirmInput.Password)
        {
            MessageText.Text = "The passwords do not match.";
            MessageText.Foreground = Brushes.Firebrick;
            return;
        }

        DialogResult = true;
    }
}
