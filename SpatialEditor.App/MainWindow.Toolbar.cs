using System.Windows;
using System.Windows.Input;
using NetTopologySuite.Geometries;
using SpatialEditor.Domain;

// ============================================================================
// MainWindow.Toolbar.cs : partial class MainWindow 중 "도구 모음 버튼 상태/전달 동작" 담당 부분
// ----------------------------------------------------------------------------
// 흐름도
//   InitializeToolbarState: CommandManager.RequerySuggested(키보드/마우스/포커스 활동 후 WPF 가 발생,
//     모든 선택·편집 변경을 포괄) 구독 → UpdateToolbarState 호출
//   UpdateToolbarState: 선택 여부(편집 모드=선택 ID 집합, 보기 모드=선택 피처),
//     편집 모드, 실행취소/다시실행 스택, 연결/로그인/권한(DrawingView/DrawingEdit)으로
//     각 버튼 IsEnabled 계산
//   버튼 클릭 → 기존 명령으로 전달: 회전(RotateSelected), 모두 선택(SelectAllObjects),
//     선택 영역 확대(ZoomToSelection_Click → 선택 Envelope 합집합 → ZoomToEnvelope)
// ============================================================================
namespace SpatialEditor.App;

/// <summary>MainWindow 의 도구 모음 부분: 기존 명령으로 전달만 하는 버튼들과 그 활성/비활성 상태.</summary>
public partial class MainWindow
{
    /// <summary>도구 모음 상태 갱신을 WPF 명령 재평가 이벤트에 연결하고 한 번 즉시 갱신합니다(창 초기화 시 1회 호출).</summary>
    private void InitializeToolbarState()
    {
        // WPF 는 키보드/마우스/포커스 활동 뒤에 이 이벤트를 발생시키므로, 모든 선택·편집 변경을 빠짐없이 감지할 수 있습니다.
        CommandManager.RequerySuggested += (_, _) => UpdateToolbarState();
        UpdateToolbarState();
    }

    /// <summary>현재 선택/편집/연결/권한 상태에 맞춰 도구 모음 버튼들의 활성 여부를 다시 계산합니다.</summary>
    private void UpdateToolbarState()
    {
        // 선택 여부: 편집 모드는 선택 ID 집합, 보기 모드는 선택된 피처 기준입니다.
        var hasSelection = isEditMode ? selectedEditIds.Count > 0 : selectedFeature is not null;
        ZoomSelectionButton.IsEnabled = hasSelection;
        SelectAllButton.IsEnabled = isEditMode && editableShapesById.Count > 0;
        UndoButton.IsEnabled = isEditMode && undoStack.Count > 0;
        RedoButton.IsEnabled = isEditMode && redoStack.Count > 0;
        RotateCwButton.IsEnabled = isEditMode && selectedEditIds.Count > 0;
        RotateCcwButton.IsEnabled = RotateCwButton.IsEnabled;
        // 도면 보기 권한이 있고 연결·로그인된 경우에만 버전/배치/비교 버튼을 켭니다(저장은 편집 권한도 필요).
        var canView = isConnected && currentUser is not null && Has(Permissions.DrawingView);
        VersionManagerButton.IsEnabled = canView;
        LayoutButton.IsEnabled = canView;
        VersionSaveButton.IsEnabled = canView && Has(Permissions.DrawingEdit);
        CompareButton.IsEnabled = canView;
        LibraryButton.IsEnabled = LibraryMenuItem.IsEnabled;
        RegisterLibraryButton.IsEnabled = RegisterLibraryMenuItem.IsEnabled && (isEditMode ? selectedEditIds.Count > 0 : selectedFeature is not null);
    }

    /// <summary>선택 객체를 시계 방향으로 회전합니다(<see cref="RotateSelected"/> 로 전달).</summary>
    /// <param name="sender">이벤트를 일으킨 버튼.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private void RotateCw_Click(object sender, RoutedEventArgs e) => RotateSelected(clockwise: true);

    /// <summary>선택 객체를 반시계 방향으로 회전합니다(<see cref="RotateSelected"/> 로 전달).</summary>
    /// <param name="sender">이벤트를 일으킨 버튼.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private void RotateCcw_Click(object sender, RoutedEventArgs e) => RotateSelected(clockwise: false);

    /// <summary>"모두 선택" 버튼: <see cref="SelectAllObjects"/> 로 전달합니다.</summary>
    /// <param name="sender">이벤트를 일으킨 버튼.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAllObjects();

    /// <summary>편집 모드에서 화면의 모든 편집 가능 객체를 선택합니다(편집 모드가 아니거나 객체가 없으면 아무 일도 하지 않음).</summary>
    private void SelectAllObjects()
    {
        if (!isEditMode || editableShapesById.Count == 0)
        {
            return;
        }

        // 기존 선택을 비우고 모든 객체 ID 를 담은 뒤, 첫 객체를 기준으로 선택 표시(그룹 유지)를 갱신합니다.
        selectedEditIds.Clear();
        foreach (var id in editableShapesById.Keys)
        {
            selectedEditIds.Add(id);
        }

        SelectEditableObject(selectedEditIds.First(), keepGroup: true);
        EditStatusText.Text = $"Selected all {selectedEditIds.Count} object(s).";
    }

    /// <summary>선택된 객체(들)의 외곽을 모두 포함하는 영역으로 화면을 확대합니다.</summary>
    /// <param name="sender">이벤트를 일으킨 버튼.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private void ZoomToSelection_Click(object sender, RoutedEventArgs e)
    {
        if (renderedEnvelope is null)
        {
            return;
        }

        // 편집 모드: 선택된 각 객체의 (편집 중이면 편집된) 외곽 Envelope 를 합칩니다. 보기 모드: 선택 피처의 Envelope 를 사용합니다.
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

        // 합칠 대상이 없으면 안내만 하고 끝냅니다.
        if (envelope.IsNull)
        {
            StatusText.Text = "Select an object first.";
            return;
        }

        ZoomToEnvelope(envelope);
    }
}
