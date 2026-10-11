using System.Windows;

namespace SpatialEditor.App;

/// <summary>
/// Undo / redo and "cancel edits" for edit mode. Every edit command pushes a snapshot of all
/// pending edits first (<see cref="PushUndo"/>); undo/redo swap the pending-edit set with those
/// snapshots, so moves, rotations, copies, groups, deletions and vertex edits all undo alike.
/// </summary>
public partial class MainWindow
{
    private const int MaxUndoSteps = 100;
    private readonly List<Dictionary<long, PendingLayerEdit>> undoStack = new();
    private readonly List<Dictionary<long, PendingLayerEdit>> redoStack = new();

    private Dictionary<long, PendingLayerEdit> SnapshotPendingEdits() =>
        pendingEdits.ToDictionary(pair => pair.Key, pair => ClonePendingEdit(pair.Value));

    private static PendingLayerEdit ClonePendingEdit(PendingLayerEdit edit) => new()
    {
        Source = edit.Source,
        IsNew = edit.IsNew,
        IsDeleted = edit.IsDeleted,
        HasChanges = edit.HasChanges,
        CurrentGeometry = edit.CurrentGeometry,
        CurrentOuterGeometry = edit.CurrentOuterGeometry,
        Attributes = new Dictionary<string, string?>(edit.Attributes)
    };

    /// <summary>Call right before an edit command changes <see cref="pendingEdits"/>.</summary>
    private void PushUndo()
    {
        undoStack.Add(SnapshotPendingEdits());
        if (undoStack.Count > MaxUndoSteps)
        {
            undoStack.RemoveAt(0);
        }

        redoStack.Clear();
    }

    private void ClearUndoHistory()
    {
        undoStack.Clear();
        redoStack.Clear();
    }

    private void RestorePendingEdits(Dictionary<long, PendingLayerEdit> snapshot)
    {
        if (editDragKind != EditDragKind.None)
        {
            editDragKind = EditDragKind.None;
            draggingVertex = null;
            editShapeSnapshot.Clear();
            editHandleSnapshot = null;
            MapCanvas.ReleaseMouseCapture();
        }

        pendingEdits.Clear();
        foreach (var (id, edit) in snapshot)
        {
            pendingEdits[id] = ClonePendingEdit(edit);
        }

        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = HasPendingEdits();
    }

    private void CancelEdits_Click(object sender, RoutedEventArgs e) => CancelEdits();

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoEdit();

    private void Redo_Click(object sender, RoutedEventArgs e) => RedoEdit();

    private void UndoEdit()
    {
        if (!isEditMode)
        {
            return;
        }

        if (undoStack.Count == 0)
        {
            EditStatusText.Text = "Nothing to undo.";
            return;
        }

        redoStack.Add(SnapshotPendingEdits());
        var snapshot = undoStack[^1];
        undoStack.RemoveAt(undoStack.Count - 1);
        RestorePendingEdits(snapshot);
        EditStatusText.Text = $"Undo ({undoStack.Count} more).";
    }

    private void RedoEdit()
    {
        if (!isEditMode)
        {
            return;
        }

        if (redoStack.Count == 0)
        {
            EditStatusText.Text = "Nothing to redo.";
            return;
        }

        undoStack.Add(SnapshotPendingEdits());
        var snapshot = redoStack[^1];
        redoStack.RemoveAt(redoStack.Count - 1);
        RestorePendingEdits(snapshot);
        EditStatusText.Text = $"Redo ({redoStack.Count} more).";
    }

    /// <summary>
    /// Esc / Ctrl+Z: cancels a drag in progress, otherwise throws away every unsaved change and
    /// restores the saved state, staying in edit mode. The discarded state can still be brought back with Undo.
    /// </summary>
    private void CancelEdits()
    {
        if (!isEditMode)
        {
            return;
        }

        if (editDragKind != EditDragKind.None)
        {
            editDragKind = EditDragKind.None;
            draggingVertex = null;
            editShapeSnapshot.Clear();
            editHandleSnapshot = null;
            boxSelectRectangle = null;
            MapCanvas.ReleaseMouseCapture();
        }

        if (HasPendingEdits())
        {
            PushUndo();
            pendingEdits.Clear();
            DeselectEditableObject();
            RenderEditableLayerObjects();
            SaveEditsButton.IsEnabled = false;
            EditStatusText.Text = "Restored to the saved state. Unsaved edits were discarded (Undo brings them back).";
            return;
        }

        DeselectEditableObject();
        RenderEditableLayerObjects();
    }
}
