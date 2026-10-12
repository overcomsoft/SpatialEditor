using System.Windows;

namespace SpatialEditor.App;

/// <summary>Asks for a name and an optional comment for a new version.</summary>
public partial class SaveVersionWindow : Window
{
    public string Label => LabelBox.Text.Trim();
    public string? Comment => string.IsNullOrWhiteSpace(CommentBox.Text) ? null : CommentBox.Text.Trim();

    public SaveVersionWindow(string layoutName, string suggestedLabel, string? comment = null, string title = "Save version", string okText = "Save version")
    {
        InitializeComponent();
        Title = title;
        OkButton.Content = okText;
        HeaderText.Text = $"Drawing: {layoutName}";
        LabelBox.Text = suggestedLabel;
        CommentBox.Text = comment ?? string.Empty;
        Loaded += (_, _) =>
        {
            LabelBox.Focus();
            LabelBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Label.Length == 0)
        {
            MessageBox.Show(this, "Enter a version name.", "Save version", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
