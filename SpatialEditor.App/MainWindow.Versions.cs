using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.App;

/// <summary>
/// Drawing (layout) and version management: the active drawing, saving versions, the Version Manager,
/// switching drawings, and the comparison overlay drawn on the 2D view.
/// </summary>
public partial class MainWindow
{
    private long activeLayoutId = 1;
    private string activeLayoutName = "기본 도면";

    private VersionRepository NewVersionRepository() => new(connectionSettings.ConnectionString);

    /// <summary>
    /// Brings the database up to the current schema (drawings, object ids, versions) right after login, before
    /// anything is read, so an older database is upgraded in place the first time this version connects.
    /// </summary>
    private async Task<bool> EnsureDrawingSchemasAsync()
    {
        try
        {
            await using (var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId))
            {
                await repository.EnsureSchemaAsync();
            }

            await using var versions = NewVersionRepository();
            await versions.EnsureSchemaAsync();
            return true;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not prepare the database: {exception.Message}";
            MessageBox.Show(this, $"Could not prepare the database schema:\n{exception.Message}", "Database", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ---------------------------------------------------------------- status

    private async Task RefreshLayoutStatusAsync()
    {
        if (!isConnected || currentUser is null)
        {
            LayoutStatusText.Text = string.Empty;
            return;
        }

        try
        {
            await using var versions = NewVersionRepository();
            var status = await versions.GetStatusAsync(activeLayoutId);
            if (status is null)
            {
                // The drawing was deleted by someone else: fall back to the default drawing.
                activeLayoutId = 1;
                status = await versions.GetStatusAsync(activeLayoutId);
            }

            if (status is null)
            {
                LayoutStatusText.Text = string.Empty;
                return;
            }

            activeLayoutName = status.LayoutName;
            LayoutStatusText.Text = status.BaseVersionNo is { } no
                ? $"도면: {status.LayoutName} · v{no} 기반{(status.IsModified ? " (수정됨)" : string.Empty)}"
                : $"도면: {status.LayoutName} · 저장된 버전 없음";
            LayoutStatusText.ToolTip = status.BaseVersionLabel is { } label
                ? $"이 도면의 작업본은 v{status.BaseVersionNo} \"{label}\"에서 시작했습니다. 클릭하면 버전 관리자가 열립니다."
                : "클릭하면 버전 관리자가 열립니다.";
        }
        catch (Exception)
        {
            LayoutStatusText.Text = string.Empty;
        }

    }

    private void LayoutStatus_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => VersionManager_Click(sender, e);

    // ---------------------------------------------------------------- versions

    /// <summary>Keeps every edit save as an automatic version (when enabled in Settings). Never blocks or fails the save.</summary>
    private async Task CreateAutoVersionAsync()
    {
        if (appSettings.AutoVersionOnSave)
        {
            try
            {
                await using var versions = NewVersionRepository();
                await versions.CreateVersionAsync(
                    activeLayoutId, $"자동 저장 {DateTime.Now:yyyy-MM-dd HH:mm:ss}", null, VersionKind.Auto, CurrentChangeContext);
            }
            catch (Exception exception)
            {
                StatusText.Text += $"  (자동 버전을 만들지 못했습니다: {exception.Message})";
            }
        }

        await RefreshLayoutStatusAsync();
    }

    private async void SaveVersion_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Permissions.DrawingEdit, "save versions") || !isConnected)
        {
            return;
        }

        if (isEditMode && HasPendingEdits())
        {
            MessageBox.Show(this, "Save your edits first (Ctrl+S). A version stores the saved drawing, not unsaved edits.", "Save version",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveVersionWindow(activeLayoutName, $"{activeLayoutName} {DateTime.Now:MM-dd HH:mm}") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            ShowBusy("Saving version...");
            await using var versions = NewVersionRepository();
            var created = await versions.CreateVersionAsync(activeLayoutId, dialog.Label, dialog.Comment, VersionKind.Manual, CurrentChangeContext);
            StatusText.Text = created is null
                ? "The drawing has not changed since the newest version; no version was created."
                : $"Saved version v{created.VersionNo} \"{created.Label}\" ({created.BlockCount} equipment).";
            if (created is not null)
            {
                await AuditAsync("version.save", "layout_version", created.Id.ToString(), $"{activeLayoutName} v{created.VersionNo} {created.Label}");
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Save version failed: {exception.Message}";
            MessageBox.Show(this, exception.Message, "Save version", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            HideBusy();
        }

        await RefreshLayoutStatusAsync();
    }

    private async void VersionManager_Click(object sender, RoutedEventArgs e)
    {
        if (!isConnected || !Require(Permissions.DrawingView, "view versions"))
        {
            return;
        }

        await using var versions = NewVersionRepository();
        var blocked = isEditMode && HasPendingEdits() ? "Save your edits first (Ctrl+S). A version stores the saved drawing, not unsaved edits." : null;
        var window = new VersionManagerWindow(
            versions, activeLayoutId, activeLayoutName, CurrentChangeContext,
            isEditMode, Has(Permissions.DrawingEdit), Has(Permissions.DrawingExport), blocked) { Owner = this };
        window.ShowDialog();

        switch (window.Outcome)
        {
            case VersionManagerOutcome.Restored:
                await AuditAsync("version.restore", "layout", activeLayoutId.ToString(), activeLayoutName);
                await ReloadForSyncAsync();
                StatusText.Text = "버전을 복원했습니다.";
                break;
            case VersionManagerOutcome.SwitchLayout when window.SwitchToLayoutId is { } id:
                await SwitchLayoutAsync(id);
                break;
            case VersionManagerOutcome.ShowDiff when window.ShownDiff is { } diff:
                ShowDiffOverlay(diff, window.ShownDiffTitle);
                break;
        }

        await RefreshLayoutStatusAsync();
    }

    private async void LayoutManager_Click(object sender, RoutedEventArgs e)
    {
        if (!isConnected || !Require(Permissions.DrawingView, "view drawings"))
        {
            return;
        }

        await using var versions = NewVersionRepository();
        var window = new LayoutManagerWindow(versions, activeLayoutId, CurrentChangeContext, Has(Permissions.DrawingEdit)) { Owner = this };
        if (window.ShowDialog() == true && window.SwitchToLayoutId is { } id)
        {
            await SwitchLayoutAsync(id);
        }
        else
        {
            await RefreshLayoutStatusAsync();
        }
    }

    /// <summary>Opens another drawing: its working copy replaces what is on screen.</summary>
    private async Task SwitchLayoutAsync(long layoutId)
    {
        if (isEditMode)
        {
            MessageBox.Show(this, "Leave edit mode (Save or Cancel) before opening another drawing.", "Drawings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        activeLayoutId = layoutId;
        activeLayerFilter = null;
        ClearDiffOverlay();
        ClearMapAndLayers();
        await StartSyncAsync();
        await LoadImportedPolygonsAsync();
        await RefreshLayoutStatusAsync();
        StatusText.Text = $"도면 \"{activeLayoutName}\"을(를) 열었습니다.";
    }

    // ---------------------------------------------------------------- comparison overlay

    private readonly List<Shape> diffOverlayShapes = new();
    private Border? diffLegend;

    private bool HasDiffOverlay => diffOverlayShapes.Count > 0 || diffLegend is not null;

    private void ClearComparison_Click(object sender, RoutedEventArgs e) => ClearDiffOverlay();

    private void CompareToolbar_Click(object sender, RoutedEventArgs e)
    {
        if (HasDiffOverlay)
        {
            ClearDiffOverlay();
            return;
        }

        VersionManager_Click(sender, e);
    }

    private void ClearDiffOverlay()
    {
        foreach (var shape in diffOverlayShapes)
        {
            baseStrokeWidths.Remove(shape);
            MapLayerCanvas.Children.Remove(shape);
        }

        diffOverlayShapes.Clear();
        if (diffLegend is not null)
        {
            MapCanvas.Children.Remove(diffLegend);
            diffLegend = null;
        }
    }

    private static readonly (DiffKind Kind, string Text, Color Color)[] DiffLegendItems =
    {
        (DiffKind.Added, "추가", Color.FromRgb(0x2E, 0x7D, 0x32)),
        (DiffKind.Removed, "삭제", Color.FromRgb(0xC6, 0x28, 0x28)),
        (DiffKind.Moved, "이동", Color.FromRgb(0xEF, 0x6C, 0x00)),
        (DiffKind.Rotated, "회전", Color.FromRgb(0x6A, 0x1B, 0x9A)),
        (DiffKind.Reshaped, "모양 교체", Color.FromRgb(0x15, 0x65, 0xC0)),
        (DiffKind.AttributesChanged, "속성", Color.FromRgb(0x00, 0x79, 0x6B))
    };

    private static Color DiffColor(DiffKind kind) => DiffLegendItems.First(item => item.Kind == kind).Color;

    private void ShowDiffOverlay(VersionDiff diff, string title)
    {
        ClearDiffOverlay();
        if (renderedEnvelope is null)
        {
            StatusText.Text = "Load layers first; the comparison is drawn over the loaded drawing.";
            return;
        }

        foreach (var entry in diff.Entries)
        {
            var color = DiffColor(entry.Kind);
            switch (entry.Kind)
            {
                case DiffKind.Added:
                    AddDiffShape(entry.AfterGeometry, color, dashed: false, fill: true);
                    break;
                case DiffKind.Removed:
                    AddDiffShape(entry.BeforeGeometry, color, dashed: true, fill: false);
                    break;
                case DiffKind.Moved:
                    AddDiffShape(entry.BeforeGeometry, Color.FromRgb(0x78, 0x90, 0x9C), dashed: true, fill: false);
                    AddDiffShape(entry.AfterGeometry, color, dashed: false, fill: false);
                    AddDiffArrow(entry.BeforeGeometry, entry.AfterGeometry, color);
                    break;
                case DiffKind.Rotated:
                case DiffKind.Reshaped:
                    AddDiffShape(entry.BeforeGeometry, Color.FromRgb(0x78, 0x90, 0x9C), dashed: true, fill: false);
                    AddDiffShape(entry.AfterGeometry, color, dashed: false, fill: false);
                    break;
                default:
                    AddDiffShape(entry.AfterGeometry ?? entry.BeforeGeometry, color, dashed: true, fill: false);
                    break;
            }
        }

        var panel = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        panel.Children.Add(new TextBlock { Text = $"비교: {title}", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = VersionManagerWindow.Summary(diff), Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var (kind, text, color) in DiffLegendItems.Where(item => diff.Count(item.Kind) > 0))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Rectangle { Width = 14, Height = 10, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 2, 6, 0) });
            row.Children.Add(new TextBlock { Text = $"{text} {diff.Count(kind)}" });
            panel.Children.Add(row);
        }

        panel.Children.Add(new TextBlock { Text = "Esc 또는 툴바 \"비교\"로 해제", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
        diffLegend = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(diffLegend, 10);
        Canvas.SetTop(diffLegend, 10);
        Panel.SetZIndex(diffLegend, 200000);
        MapCanvas.Children.Add(diffLegend);
        StatusText.Text = $"비교 표시 중: {title} — {VersionManagerWindow.Summary(diff)}";
    }

    private void AddDiffShape(NtsGeometry? geometry, Color color, bool dashed, bool fill)
    {
        if (geometry is null || geometry.IsEmpty)
        {
            return;
        }

        var parts = new List<NtsGeometry>();
        CollectGeometryParts(geometry, parts);
        var path = new Path
        {
            Data = BuildHighlightGeometry(geometry.Factory.CreateGeometryCollection(parts.ToArray())),
            Stroke = new SolidColorBrush(color),
            Fill = fill ? new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B)) : Brushes.Transparent,
            IsHitTestVisible = false
        };
        if (dashed)
        {
            path.StrokeDashArray = new DoubleCollection { 4, 3 };
        }

        ApplyRoundStroke(path);
        RegisterDiffShape(path, 2.0);
    }

    private void AddDiffArrow(NtsGeometry? from, NtsGeometry? to, Color color)
    {
        if (from is null || to is null || from.IsEmpty || to.IsEmpty)
        {
            return;
        }

        var a = ProjectToScreen(from.Centroid.Coordinate);
        var b = ProjectToScreen(to.Centroid.Coordinate);
        var path = new Path
        {
            Data = new LineGeometry(a, b),
            Stroke = new SolidColorBrush(color),
            IsHitTestVisible = false
        };
        ApplyRoundStroke(path);
        RegisterDiffShape(path, 1.5);
    }

    private void RegisterDiffShape(Shape shape, double width)
    {
        baseStrokeWidths[shape] = width;
        shape.StrokeThickness = width / Math.Max(mapScale.ScaleX, 0.1);
        Panel.SetZIndex(shape, 99000);
        MapLayerCanvas.Children.Add(shape);
        diffOverlayShapes.Add(shape);
    }
}
