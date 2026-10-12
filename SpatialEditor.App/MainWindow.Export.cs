using System.IO;
using System.Windows;
using Microsoft.Win32;
using SpatialEditor.Cad;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.App;

public partial class MainWindow
{
    /// <summary>
    /// Exports the equipment block objects to a DXF: one BLOCK per block name, one INSERT per
    /// object, with name / rotation / position point written as block attributes. While in edit
    /// mode the current (unsaved) edited state is exported; otherwise the stored objects are.
    /// </summary>
    private async void ExportDxf_Click(object sender, RoutedEventArgs e)
    {
        if (!Require(Permissions.DrawingExport, "export DXF data"))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export equipment as DXF blocks",
            Filter = "DXF drawing (*.dxf)|*.dxf",
            DefaultExt = ".dxf",
            FileName = $"Equipment_{DateTime.Now:yyyyMMdd_HHmmss}.dxf",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ShowBusy("Exporting DXF...");
            var instances = isEditMode ? CollectEditedInstances() : await LoadStoredInstancesAsync();
            if (instances.Count == 0)
            {
                StatusText.Text = "Export skipped: no block objects to export.";
                MessageBox.Show(this, "There are no equipment block objects to export.", "Export DXF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var written = await Task.Run(() => new DxfBlockExporter().Export(dialog.FileName, instances));
            StatusText.Text = $"Exported {written} block object(s) to {dialog.FileName}";
            await AuditAsync("export.dxf", "dxf", Path.GetFileName(dialog.FileName), $"{written} block object(s)");
        }
        catch (Exception ex)
        {
            StatusText.Text = "DXF export failed.";
            MessageBox.Show(this, ex.Message, "Export DXF failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            HideBusy();
        }
    }

    private void SelectedLayerDelete_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedLayer(out var layerName))
        {
            _ = DeleteLayerAsync(layerName);
        }
    }

    /// <summary>Permanently deletes a layer (all its objects) from the database after confirmation.</summary>
    private async Task DeleteLayerAsync(string layerName)
    {
        if (!Require(Permissions.DrawingDelete, "delete layers") || !CanReloadLayers())
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Delete layer \"{layerName}\" and every object on it from the database? This cannot be undone.",
            "Delete layer", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            StatusText.Text = "Delete cancelled.";
            return;
        }

        try
        {
            ShowBusy($"Deleting layer {layerName}...");
            connectionSettings = ReadConnectionSettingsFromUi();
            int removed;
            await using (var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId))
            {
                removed = await repository.DeleteLayerAsync(layerName, changeContext: CurrentChangeContext);
            }

            importLogger.Write($"DELETE layer={layerName} rows={removed}");
            activeLayerFilter?.RemoveAll(name => string.Equals(name, layerName, StringComparison.OrdinalIgnoreCase));
            hiddenLayers.Remove(layerName);
            await ReloadForSyncAsync();
            StatusText.Text = $"Layer \"{layerName}\" deleted ({removed} object(s)).";
            await AuditAsync("layer.delete", "layer", layerName, $"{removed} object(s)");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Delete layer failed: {ex.Message}";
        }
        finally
        {
            HideBusy();
        }
    }

    /// <summary>Current edit-mode state (pending edits applied, deleted objects skipped).</summary>
    private List<CadExportInstance> CollectEditedInstances()
    {
        var items = new List<(bool Changed, CadExportInstance Instance)>();
        foreach (var (id, source) in editableSourcesById)
        {
            if (source.FeatureType != "block_instance")
            {
                continue;
            }

            pendingEdits.TryGetValue(id, out var edit);
            if (edit?.IsDeleted == true)
            {
                continue;
            }

            var instance = ToExportInstance(source, edit?.CurrentGeometry ?? source.Geometry, edit?.Attributes ?? source.Attributes);
            if (instance is not null)
            {
                items.Add((edit?.HasChanges == true, instance));
            }
        }

        // New (unsaved) duplicates / groups only exist as pending edits.
        foreach (var (id, edit) in pendingEdits)
        {
            if (!edit.IsNew || edit.IsDeleted || editableSourcesById.ContainsKey(id) || edit.Source.FeatureType != "block_instance")
            {
                continue;
            }

            var instance = ToExportInstance(edit.Source, edit.CurrentGeometry, edit.Attributes);
            if (instance is not null)
            {
                items.Add((true, instance));
            }
        }

        // Unedited objects first: the first INSERT of a block name defines that block's geometry.
        return items.OrderBy(item => item.Changed).Select(item => item.Instance).ToList();
    }

    private async Task<List<CadExportInstance>> LoadStoredInstancesAsync()
    {
        connectionSettings = ReadConnectionSettingsFromUi();
        await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId);
        var stored = await repository.FindEditableBlockInstancesAsync();
        return stored
            .Where(source => !hiddenLayers.Contains(source.LayerName ?? "(no layer)"))
            .Select(source => ToExportInstance(source, source.Geometry, source.Attributes))
            .OfType<CadExportInstance>()
            .ToList();
    }

    private static CadExportInstance? ToExportInstance(
        EditableLayerObject source, NetTopologySuite.Geometries.Geometry geometry, IReadOnlyDictionary<string, string?> attributes)
    {
        if (!TryGetDouble(attributes, "block_insert_x", out var x) || !TryGetDouble(attributes, "block_insert_y", out var y))
        {
            return null;
        }

        var rotation = TryGetDouble(attributes, "block_rotation_deg", out var r) ? r : 0.0;
        var scaleX = TryGetDouble(attributes, "block_scale_x", out var sx) && sx != 0 ? sx : 1.0;
        var scaleY = TryGetDouble(attributes, "block_scale_y", out var sy) && sy != 0 ? sy : 1.0;
        var name = !string.IsNullOrWhiteSpace(source.BlockName)
            ? source.BlockName!
            : attributes.TryGetValue("block_name", out var attributeName) && !string.IsNullOrWhiteSpace(attributeName)
                ? attributeName!
                : "BLOCK";
        return new CadExportInstance(name, source.LayerName, x, y, rotation, scaleX, scaleY, geometry);
    }
}
