using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SpatialEditor.Cad;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.App;

public enum VersionManagerOutcome
{
    None,
    /// <summary>The working copy was replaced; the map must be reloaded.</summary>
    Restored,
    /// <summary>A new version was saved (status bar needs a refresh).</summary>
    VersionsChanged,
    /// <summary>Open another drawing (a branch was just created, or...).</summary>
    SwitchLayout,
    /// <summary>Draw <see cref="VersionManagerWindow.ShownDiff"/> over the 2D view.</summary>
    ShowDiff
}

/// <summary>
/// The version list of one drawing: save a new version, compare two versions (or a version with the
/// working copy), restore, branch into a new drawing, set the baseline, archive, rename, delete, export.
/// Actions that change what the main window shows end the dialog with an <see cref="Outcome"/>.
/// </summary>
public partial class VersionManagerWindow : Window
{
    private sealed record VersionRow(
        LayoutVersion Version, string No, string Label, string KindText, string StatusText, string Author, string Time,
        int BlockCount, int FeatureCount, string Parent, string Comment);

    private sealed record DiffRow(DiffEntry Entry, string KindText, string Name, string Layer, string Before, string After);

    private readonly VersionRepository versions;
    private readonly long layoutId;
    private readonly string layoutName;
    private readonly ChangeContext context;
    private readonly bool isEditing;
    private readonly bool canEdit;
    private readonly bool canExport;
    private readonly string? saveBlockedReason;
    private readonly Dictionary<long, LayoutVersion> byId = new();
    private bool loaded;

    public VersionManagerOutcome Outcome { get; private set; }
    public long? SwitchToLayoutId { get; private set; }
    public VersionDiff? ShownDiff { get; private set; }
    public string ShownDiffTitle { get; private set; } = string.Empty;

    public VersionManagerWindow(
        VersionRepository versions, long layoutId, string layoutName, ChangeContext context,
        bool isEditing, bool canEdit, bool canExport, string? saveBlockedReason)
    {
        InitializeComponent();
        this.versions = versions;
        this.layoutId = layoutId;
        this.layoutName = layoutName;
        this.context = context;
        this.isEditing = isEditing;
        this.canEdit = canEdit;
        this.canExport = canExport;
        this.saveBlockedReason = saveBlockedReason;
        HeaderText.Text = $"Drawing: {layoutName}";
        Loaded += async (_, _) =>
        {
            loaded = true;
            await ReloadAsync();
        };
    }

    // ---------------------------------------------------------------- list

    private static string KindText(string kind) => kind switch
    {
        VersionKind.Auto => "auto",
        VersionKind.PreRestore => "pre-restore",
        _ => "manual"
    };

    private static string StatusText(string status) => status switch
    {
        VersionStatus.Baseline => "BASELINE",
        VersionStatus.Released => "released",
        VersionStatus.Archived => "archived",
        _ => "draft"
    };

    private async Task ReloadAsync(long? select = null)
    {
        try
        {
            var all = await versions.ListVersionsAsync(layoutId, ShowAutoCheck.IsChecked == true);
            byId.Clear();
            foreach (var version in all)
            {
                byId[version.Id] = version;
            }

            // The "From" column shows the parent's number even when that parent is hidden by the filter.
            var numbers = (await versions.ListVersionsAsync(layoutId, true)).ToDictionary(version => version.Id, version => version.VersionNo);
            var rows = all.Select(version => new VersionRow(
                version, $"v{version.VersionNo}", version.Label, KindText(version.Kind), StatusText(version.Status),
                version.CreatedByName ?? "-", version.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                version.BlockCount, version.FeatureCount,
                version.ParentVersionId is { } parent && numbers.TryGetValue(parent, out var no) ? $"v{no}" : "-",
                (version.Comment ?? string.Empty).Replace('\n', ' '))).ToList();
            VersionList.ItemsSource = rows;
            if (select is { } id && rows.FirstOrDefault(row => row.Version.Id == id) is { } target)
            {
                VersionList.SelectedItem = target;
            }

            MessageText.Text = rows.Count == 0 ? "No version has been saved yet. Use \"Save new version…\"." : $"{rows.Count} version(s).";
            UpdateButtons();
        }
        catch (Exception exception)
        {
            MessageText.Text = $"Could not load versions: {exception.Message}";
        }
    }

    private List<VersionRow> SelectedRows => VersionList.SelectedItems.OfType<VersionRow>().ToList();

    private LayoutVersion? SingleSelected => SelectedRows is { Count: 1 } rows ? rows[0].Version : null;

    private void UpdateButtons()
    {
        if (!loaded)
        {
            return;
        }

        var selected = SelectedRows;
        var one = selected.Count == 1;
        SaveButton.IsEnabled = canEdit;
        CompareButton.IsEnabled = selected.Count is 1 or 2;
        RestoreButton.IsEnabled = canEdit && !isEditing && one;
        BranchButton.IsEnabled = canEdit && one;
        BaselineButton.IsEnabled = canEdit && one;
        ArchiveButton.IsEnabled = canEdit && one && !(one && selected[0].Version.Status == VersionStatus.Baseline);
        EditButton.IsEnabled = canEdit && one && selected[0].Version.Status != VersionStatus.Baseline;
        DeleteButton.IsEnabled = canEdit && one && selected[0].Version.Status != VersionStatus.Baseline;
        ExportButton.IsEnabled = canExport && one;
    }

    private void VersionList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();

    private void VersionList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CompareButton.IsEnabled)
        {
            Compare_Click(sender, e);
        }
    }

    private async void ShowAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (loaded)
        {
            await ReloadAsync();
        }
    }

    // ---------------------------------------------------------------- save / edit

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (saveBlockedReason is not null)
        {
            MessageBox.Show(this, saveBlockedReason, "Save version", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveVersionWindow(layoutName, $"{layoutName} {DateTime.Now:MM-dd HH:mm}") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var created = await versions.CreateVersionAsync(layoutId, dialog.Label, dialog.Comment, VersionKind.Manual, context);
            if (created is null)
            {
                MessageText.Text = "The drawing is identical to the newest version. Nothing was saved.";
                MessageBox.Show(this, "The drawing has not changed since the newest version, so no new version was created.", "Save version",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Outcome = VersionManagerOutcome.VersionsChanged;
            await ReloadAsync(created.Id);
            MessageText.Text = $"Saved v{created.VersionNo} \"{created.Label}\" ({created.BlockCount} equipment, {created.FeatureCount} other objects).";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Save version", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected is not { } version)
        {
            return;
        }

        var dialog = new SaveVersionWindow(layoutName, version.Label, version.Comment, "Rename version", "OK") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await versions.UpdateVersionAsync(version.Id, dialog.Label, dialog.Comment);
            await ReloadAsync(version.Id);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Rename version", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Baseline_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected is not { } version)
        {
            return;
        }

        try
        {
            if (version.Status == VersionStatus.Baseline)
            {
                await versions.SetStatusAsync(version.Id, VersionStatus.Released);
                MessageText.Text = $"v{version.VersionNo} is no longer the baseline.";
            }
            else
            {
                var answer = MessageBox.Show(this,
                    $"Make v{version.VersionNo} \"{version.Label}\" the baseline? A baseline cannot be renamed or deleted until it is released again. The current baseline (if any) becomes \"released\".",
                    "Baseline", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }

                await versions.SetStatusAsync(version.Id, VersionStatus.Baseline);
                MessageText.Text = $"v{version.VersionNo} is now the baseline.";
            }

            Outcome = VersionManagerOutcome.VersionsChanged;
            await ReloadAsync(version.Id);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Baseline", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected is not { } version)
        {
            return;
        }

        try
        {
            await versions.SetStatusAsync(version.Id, version.Status == VersionStatus.Archived ? VersionStatus.Draft : VersionStatus.Archived);
            await ReloadAsync(version.Id);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Archive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected is not { } version)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Delete v{version.VersionNo} \"{version.Label}\"? Its history link is kept (versions made from it are attached to its predecessor). This cannot be undone.",
            "Delete version", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await versions.DeleteVersionAsync(version.Id);
            Outcome = VersionManagerOutcome.VersionsChanged;
            DiffList.ItemsSource = null;
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Delete version", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------------------------------------------------------- restore / branch

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected is not { } version)
        {
            return;
        }

        if (isEditing)
        {
            MessageBox.Show(this, "Leave edit mode (Save or Cancel) before restoring a version.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Restore v{version.VersionNo} \"{version.Label}\" as the working copy of \"{layoutName}\"?\n\n" +
            "If the current drawing differs from the newest version it is saved first as an automatic version, so the restore can be undone. " +
            "Other users will be told that the data changed.",
            "Restore version", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var safety = await versions.RestoreVersionAsync(layoutId, version.Id, context);
            Outcome = VersionManagerOutcome.Restored;
            MessageText.Text = safety is null
                ? $"Restored v{version.VersionNo}."
                : $"Restored v{version.VersionNo}. The previous state was kept as v{safety.VersionNo}.";
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Restore version", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Branch_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected is not { } version)
        {
            return;
        }

        var name = PromptWindow.Ask(this, "Branch to new drawing", $"Name of the new drawing (copy of v{version.VersionNo}):", $"{layoutName} - v{version.VersionNo} 분기");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var newId = await versions.CreateLayoutFromVersionAsync(version.Id, name.Trim(), null, context);
            var open = MessageBox.Show(this, $"The drawing \"{name.Trim()}\" was created from v{version.VersionNo}. Open it now?", "Branch",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (open == MessageBoxResult.Yes)
            {
                Outcome = VersionManagerOutcome.SwitchLayout;
                SwitchToLayoutId = newId;
                DialogResult = true;
            }
            else
            {
                MessageText.Text = $"Created the drawing \"{name.Trim()}\". Open it from the Drawings window.";
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Branch", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- compare

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count is not (1 or 2))
        {
            MessageBox.Show(this, "Select one version (to compare with the working copy) or two versions.", "Compare", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LayoutVersion from;
        LayoutVersion? to;
        if (rows.Count == 2)
        {
            var ordered = rows.OrderBy(row => row.Version.VersionNo).ToList();
            from = ordered[0].Version;
            to = ordered[1].Version;
        }
        else
        {
            from = rows[0].Version;
            to = null;
        }

        try
        {
            var diff = await versions.DiffAsync(layoutId, from.Id, to?.Id);
            ShownDiff = diff;
            ShownDiffTitle = to is null ? $"v{from.VersionNo} → working copy" : $"v{from.VersionNo} → v{to.VersionNo}";
            DiffList.ItemsSource = diff.Entries.Select(entry => new DiffRow(
                entry, KindLabel(entry.Kind), entry.Name, entry.LayerName ?? "-", entry.Before ?? string.Empty, entry.After ?? string.Empty)).ToList();
            DiffHeaderText.Text = $"{ShownDiffTitle}:  {Summary(diff)}";
            ShowInViewButton.IsEnabled = !diff.IsEmpty;
            CsvButton.IsEnabled = !diff.IsEmpty;
            MessageText.Text = diff.IsEmpty ? "No differences." : $"{diff.Entries.Count} difference(s).";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Compare", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal static string KindLabel(DiffKind kind) => kind switch
    {
        DiffKind.Added => "added",
        DiffKind.Removed => "removed",
        DiffKind.Moved => "moved",
        DiffKind.Rotated => "rotated",
        DiffKind.Reshaped => "replaced",
        _ => "attributes"
    };

    internal static string Summary(VersionDiff diff) =>
        diff.IsEmpty
            ? "identical"
            : string.Join(" · ", new[]
            {
                (DiffKind.Added, "added"), (DiffKind.Removed, "removed"), (DiffKind.Moved, "moved"),
                (DiffKind.Rotated, "rotated"), (DiffKind.Reshaped, "replaced"), (DiffKind.AttributesChanged, "attributes")
            }.Where(pair => diff.Count(pair.Item1) > 0).Select(pair => $"{pair.Item2} {diff.Count(pair.Item1)}"));

    private void ShowInView_Click(object sender, RoutedEventArgs e)
    {
        if (ShownDiff is null || ShownDiff.IsEmpty)
        {
            return;
        }

        Outcome = VersionManagerOutcome.ShowDiff;
        DialogResult = true;
    }

    private void Csv_Click(object sender, RoutedEventArgs e)
    {
        if (ShownDiff is null)
        {
            return;
        }

        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"diff_{DateTime.Now:yyyyMMdd_HHmmss}.csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string Cell(string? text) => "\"" + (text ?? string.Empty).Replace("\"", "\"\"") + "\"";
        var csv = new StringBuilder();
        csv.AppendLine("change,object,layer,before,after");
        foreach (var entry in ShownDiff.Entries)
        {
            csv.AppendLine(string.Join(",", Cell(KindLabel(entry.Kind)), Cell(entry.Name), Cell(entry.LayerName), Cell(entry.Before), Cell(entry.After)));
        }

        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
        MessageText.Text = $"Wrote {ShownDiff.Entries.Count} row(s) to {dialog.FileName}";
    }

    // ---------------------------------------------------------------- export

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected is not { } version)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export version as DXF blocks",
            Filter = "DXF drawing (*.dxf)|*.dxf",
            FileName = $"{layoutName}_v{version.VersionNo}.dxf",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var blocks = await versions.GetVersionBlocksAsync(version.Id);
            var instances = blocks
                .Where(item => item.InsertX is not null && item.InsertY is not null)
                .Select(item => new CadExportInstance(
                    item.BlockName, item.LayerName, item.InsertX!.Value, item.InsertY!.Value, item.RotationDeg,
                    item.ScaleX, item.ScaleY, item.Geometry))
                .ToList();
            if (instances.Count == 0)
            {
                MessageBox.Show(this, "This version has no equipment blocks to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var written = await Task.Run(() => new DxfBlockExporter().Export(dialog.FileName, instances));
            MessageText.Text = $"Exported {written} equipment block(s) of v{version.VersionNo} to {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
