using System.Windows;
using System.Windows.Input;
using NetTopologySuite.Geometries;
using SpatialEditor.Domain;

namespace SpatialEditor.App;

/// <summary>Toolbar buttons that only forward to existing commands, plus their enabled state.</summary>
public partial class MainWindow
{
    private void InitializeToolbarState()
    {
        // WPF raises this after keyboard/mouse/focus activity, which covers every selection and edit change.
        CommandManager.RequerySuggested += (_, _) => UpdateToolbarState();
        UpdateToolbarState();
    }

    private void UpdateToolbarState()
    {
        var hasSelection = isEditMode ? selectedEditIds.Count > 0 : selectedFeature is not null;
        ZoomSelectionButton.IsEnabled = hasSelection;
        SelectAllButton.IsEnabled = isEditMode && editableShapesById.Count > 0;
        UndoButton.IsEnabled = isEditMode && undoStack.Count > 0;
        RedoButton.IsEnabled = isEditMode && redoStack.Count > 0;
        RotateCwButton.IsEnabled = isEditMode && selectedEditIds.Count > 0;
        RotateCcwButton.IsEnabled = RotateCwButton.IsEnabled;
        var canView = isConnected && currentUser is not null && Has(Permissions.DrawingView);
        VersionManagerButton.IsEnabled = canView;
        LayoutButton.IsEnabled = canView;
        VersionSaveButton.IsEnabled = canView && Has(Permissions.DrawingEdit);
        CompareButton.IsEnabled = canView;
        LibraryButton.IsEnabled = LibraryMenuItem.IsEnabled;
        RegisterLibraryButton.IsEnabled = RegisterLibraryMenuItem.IsEnabled && (isEditMode ? selectedEditIds.Count > 0 : selectedFeature is not null);
    }

    private void RotateCw_Click(object sender, RoutedEventArgs e) => RotateSelected(clockwise: true);

    private void RotateCcw_Click(object sender, RoutedEventArgs e) => RotateSelected(clockwise: false);

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAllObjects();

    private void SelectAllObjects()
    {
        if (!isEditMode || editableShapesById.Count == 0)
        {
            return;
        }

        selectedEditIds.Clear();
        foreach (var id in editableShapesById.Keys)
        {
            selectedEditIds.Add(id);
        }

        SelectEditableObject(selectedEditIds.First(), keepGroup: true);
        EditStatusText.Text = $"Selected all {selectedEditIds.Count} object(s).";
    }

    private void ZoomToSelection_Click(object sender, RoutedEventArgs e)
    {
        if (renderedEnvelope is null)
        {
            return;
        }

        var envelope = new Envelope();
        if (isEditMode)
        {
            foreach (var id in selectedEditIds)
            {
                var outer = pendingEdits.TryGetValue(id, out var edit) ? edit.CurrentOuterGeometry : GetEditableSource(id).OuterGeometry;
                envelope.ExpandToInclude(outer.EnvelopeInternal);
            }
        }
        else if (selectedFeature is not null)
        {
            envelope.ExpandToInclude(selectedFeature.Geometry.EnvelopeInternal);
        }

        if (envelope.IsNull)
        {
            StatusText.Text = "Select an object first.";
            return;
        }

        ZoomToEnvelope(envelope);
    }
}
