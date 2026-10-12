using System.Windows;

// ============================================================================
// MainWindow.Undo.cs : partial class MainWindow 중 "편집 모드 실행 취소/다시 실행/편집 취소" 담당 부분
// ----------------------------------------------------------------------------
// 흐름도 (스냅샷 방식: 편집 명령마다 '대기 중 편집 전체'를 통째로 복사해 보관)
//   편집 명령(이동/회전/복제/그룹/삭제/꼭짓점 편집) 실행 직전 → PushUndo
//     → SnapshotPendingEdits(깊은 복사: ClonePendingEdit) 를 undoStack 에 추가
//       (최대 MaxUndoSteps=100, 초과 시 가장 오래된 것 제거) → redoStack 비움
//   실행 취소 UndoEdit: 현재 상태를 redoStack 에 저장 → undoStack 마지막 스냅샷 꺼냄 → RestorePendingEdits
//   다시 실행 RedoEdit: 현재 상태를 undoStack 에 저장 → redoStack 마지막 스냅샷 꺼냄 → RestorePendingEdits
//   RestorePendingEdits: 진행 중인 드래그 중단 → pendingEdits 를 스냅샷 사본으로 교체
//     → 편집 객체 다시 그리기 → 저장 버튼 활성 여부 갱신
//   편집 취소 CancelEdits (Esc/Ctrl+Z): 드래그 중이면 드래그만 중단 → 미저장 변경이 있으면
//     PushUndo 후 전부 버리고 저장 상태로 복원(Undo 로 되살릴 수 있음)
//   ClearUndoHistory: 저장/모드 전환 등에서 이력 비우기
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// MainWindow 의 편집 모드 실행 취소/다시 실행/편집 취소 부분. 모든 편집 명령은 먼저 대기 중 편집 전체의
/// 스냅샷을 쌓고(<see cref="PushUndo"/>), 실행 취소/다시 실행은 대기 중 편집 집합을 그 스냅샷과 교체하므로
/// 이동, 회전, 복제, 그룹, 삭제, 꼭짓점 편집이 모두 똑같이 취소됩니다.
/// </summary>
public partial class MainWindow
{
    /// <summary>보관할 최대 실행 취소 단계 수(초과하면 가장 오래된 스냅샷부터 버림).</summary>
    private const int MaxUndoSteps = 100;
    /// <summary>실행 취소용 스냅샷 스택(키: 객체 ID, 값: 그 시점의 대기 중 편집). 끝이 가장 최근.</summary>
    private readonly List<Dictionary<long, PendingLayerEdit>> undoStack = new();
    /// <summary>다시 실행용 스냅샷 스택. 새 편집이 생기면(PushUndo) 비워집니다.</summary>
    private readonly List<Dictionary<long, PendingLayerEdit>> redoStack = new();

    /// <summary>현재 대기 중 편집(<c>pendingEdits</c>) 전체의 깊은 복사본(스냅샷)을 만듭니다.</summary>
    /// <returns>객체 ID → 복제된 편집 사전.</returns>
    private Dictionary<long, PendingLayerEdit> SnapshotPendingEdits() =>
        pendingEdits.ToDictionary(pair => pair.Key, pair => ClonePendingEdit(pair.Value));

    /// <summary>대기 중 편집 1건을 복제합니다. 속성 사전은 새로 복사하고, 지오메트리/원본 참조는 공유합니다(불변으로 취급).</summary>
    /// <param name="edit">복제할 편집. null 불가.</param>
    /// <returns>독립된 속성 사전을 가진 새 편집 객체.</returns>
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

    /// <summary>편집 명령이 <see cref="pendingEdits"/> 를 바꾸기 직전에 호출합니다: 현재 상태를 스냅샷으로 쌓고 다시 실행 이력을 지웁니다.</summary>
    private void PushUndo()
    {
        // 1) 변경 전 상태를 저장하고, 한도(MaxUndoSteps)를 넘으면 가장 오래된 단계부터 버립니다.
        undoStack.Add(SnapshotPendingEdits());
        if (undoStack.Count > MaxUndoSteps)
        {
            undoStack.RemoveAt(0);
        }

        // 2) 새 편집이 생겼으므로 이전의 '다시 실행' 가지는 더 이상 유효하지 않습니다.
        redoStack.Clear();
    }

    /// <summary>실행 취소/다시 실행 이력을 모두 비웁니다(저장 완료, 모드 전환 등 이력이 의미 없어질 때).</summary>
    private void ClearUndoHistory()
    {
        undoStack.Clear();
        redoStack.Clear();
    }

    /// <summary>대기 중 편집 집합을 주어진 스냅샷으로 교체하고 화면을 다시 그립니다.</summary>
    /// <param name="snapshot">복원할 스냅샷(내부에서 다시 복제하므로 원본 스냅샷은 변하지 않음). null 불가.</param>
    private void RestorePendingEdits(Dictionary<long, PendingLayerEdit> snapshot)
    {
        // 1) 드래그 도중이면 끊어진 상태가 남지 않도록 드래그 관련 상태와 마우스 캡처를 먼저 해제합니다.
        if (editDragKind != EditDragKind.None)
        {
            editDragKind = EditDragKind.None;
            draggingVertex = null;
            editShapeSnapshot.Clear();
            editHandleSnapshot = null;
            MapCanvas.ReleaseMouseCapture();
        }

        // 2) 스냅샷 내용으로 대기 중 편집을 교체하고(복제본 사용), 화면과 저장 버튼 상태를 맞춥니다.
        pendingEdits.Clear();
        foreach (var (id, edit) in snapshot)
        {
            pendingEdits[id] = ClonePendingEdit(edit);
        }

        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = HasPendingEdits();
    }

    /// <summary>"편집 취소" 명령 버튼/메뉴: <see cref="CancelEdits"/> 로 전달합니다.</summary>
    /// <param name="sender">이벤트를 일으킨 컨트롤.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private void CancelEdits_Click(object sender, RoutedEventArgs e) => CancelEdits();

    /// <summary>"실행 취소" 버튼/메뉴: <see cref="UndoEdit"/> 로 전달합니다.</summary>
    /// <param name="sender">이벤트를 일으킨 컨트롤.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private void Undo_Click(object sender, RoutedEventArgs e) => UndoEdit();

    /// <summary>"다시 실행" 버튼/메뉴: <see cref="RedoEdit"/> 로 전달합니다.</summary>
    /// <param name="sender">이벤트를 일으킨 컨트롤.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private void Redo_Click(object sender, RoutedEventArgs e) => RedoEdit();

    /// <summary>직전 편집을 되돌립니다. 편집 모드가 아니면 무시, 이력이 없으면 안내만 표시합니다.</summary>
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

        // 현재 상태를 다시 실행 스택에 보관한 뒤, 가장 최근 스냅샷을 꺼내 복원합니다.
        redoStack.Add(SnapshotPendingEdits());
        var snapshot = undoStack[^1];
        undoStack.RemoveAt(undoStack.Count - 1);
        RestorePendingEdits(snapshot);
        EditStatusText.Text = $"Undo ({undoStack.Count} more).";
    }

    /// <summary>되돌린 편집을 다시 적용합니다. 편집 모드가 아니면 무시, 이력이 없으면 안내만 표시합니다.</summary>
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

        // 현재 상태를 실행 취소 스택에 보관한 뒤, 다시 실행 스택의 가장 최근 스냅샷을 꺼내 복원합니다(PushUndo 와 달리 다시 실행 이력은 유지).
        undoStack.Add(SnapshotPendingEdits());
        var snapshot = redoStack[^1];
        redoStack.RemoveAt(redoStack.Count - 1);
        RestorePendingEdits(snapshot);
        EditStatusText.Text = $"Redo ({redoStack.Count} more).";
    }

    /// <summary>
    /// Esc / Ctrl+Z: 진행 중인 드래그가 있으면 그것만 취소하고, 없으면 저장되지 않은 모든 변경을 버려
    /// 저장된 상태로 복원합니다(편집 모드는 유지). 버린 상태는 실행 취소로 되살릴 수 있습니다.
    /// </summary>
    private void CancelEdits()
    {
        if (!isEditMode)
        {
            return;
        }

        // 1) 드래그 도중이면 드래그만 중단합니다(선택 상자 포함). 이 경우 아래에서 변경 내용도 함께 정리됩니다.
        if (editDragKind != EditDragKind.None)
        {
            editDragKind = EditDragKind.None;
            draggingVertex = null;
            editShapeSnapshot.Clear();
            editHandleSnapshot = null;
            boxSelectRectangle = null;
            MapCanvas.ReleaseMouseCapture();
        }

        // 2) 미저장 변경이 있으면 되살릴 수 있게 스냅샷을 남긴 뒤 모두 버립니다. 없으면 선택만 해제하고 다시 그립니다.
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
