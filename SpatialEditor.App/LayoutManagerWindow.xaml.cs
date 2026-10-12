using System.Windows;
using System.Windows.Input;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.App;

/// <summary>Lists the drawings (layouts) of the server and lets the user switch, create, rename or delete one.</summary>
public partial class LayoutManagerWindow : Window
{
    private sealed record Row(LayoutInfo Layout, string Marker, string Name, int ObjectCount, string Origin, string Created);

    private readonly VersionRepository versions;
    private readonly long activeLayoutId;
    private readonly ChangeContext context;
    private readonly bool canChange;

    /// <summary>Drawing the user chose to open (null when the window was just closed).</summary>
    public long? SwitchToLayoutId { get; private set; }

    public bool Changed { get; private set; }

    public LayoutManagerWindow(VersionRepository versions, long activeLayoutId, ChangeContext context, bool canChange)
    {
        InitializeComponent();
        this.versions = versions;
        this.activeLayoutId = activeLayoutId;
        this.context = context;
        this.canChange = canChange;
        RenameButton.IsEnabled = DeleteButton.IsEnabled = canChange;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private Row? Selected => LayoutList.SelectedItem as Row;

    private async Task ReloadAsync()
    {
        try
        {
            var layouts = await versions.ListLayoutsAsync();
            var rows = new List<Row>();
            foreach (var layout in layouts)
            {
                string origin = "-";
                if (layout.BaseVersionId is { } baseId && await versions.GetVersionAsync(baseId) is { } baseVersion)
                {
                    origin = $"v{baseVersion.VersionNo} {baseVersion.Label}";
                }

                rows.Add(new Row(layout, layout.Id == activeLayoutId ? "●" : string.Empty, layout.Name, layout.ObjectCount, origin,
                    layout.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
            }

            LayoutList.ItemsSource = rows;
            LayoutList.SelectedItem = rows.FirstOrDefault(row => row.Layout.Id == activeLayoutId) ?? rows.FirstOrDefault();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Drawings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LayoutList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OpenButton.IsEnabled = Selected is not null;

    private void LayoutList_DoubleClick(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        SwitchToLayoutId = row.Layout.Id == activeLayoutId ? null : row.Layout.Id;
        DialogResult = true;
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (!canChange)
        {
            MessageBox.Show(this, "You do not have permission to create drawings.", "Drawings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = PromptWindow.Ask(this, "New drawing", "Name of the new (empty) drawing:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            await versions.CreateLayoutAsync(name.Trim(), null, context);
            Changed = true;
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Drawings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        var name = PromptWindow.Ask(this, "Rename drawing", "New name:", row.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == row.Name)
        {
            return;
        }

        try
        {
            await versions.RenameLayoutAsync(row.Layout.Id, name.Trim(), row.Layout.Description);
            Changed = true;
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Drawings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        if (row.Layout.Id == activeLayoutId)
        {
            MessageBox.Show(this, "Open another drawing first. The drawing you are working in cannot be deleted.", "Drawings",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Delete the drawing \"{row.Name}\" with its working copy and ALL of its versions? This cannot be undone.",
            "Delete drawing", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (!await versions.DeleteLayoutAsync(row.Layout.Id))
            {
                MessageBox.Show(this, "The last remaining drawing cannot be deleted.", "Drawings", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Changed = true;
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Drawings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
