using System.Windows;
using System.Windows.Input;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

// ============================================================================
// LayoutManagerWindow : 서버의 도면(레이아웃) 목록 관리 대화상자 (열기/신규/이름변경/삭제)
// ----------------------------------------------------------------------------
// [호출 위치] MainWindow.Versions.cs 의 LayoutManager_Click (메뉴 Version > Drawings..., 도구모음 LayoutButton)
//   var window = new LayoutManagerWindow(versions, activeLayoutId, CurrentChangeContext, Has(Permissions.DrawingEdit)) { Owner = this };
//   if (window.ShowDialog() == true && window.SwitchToLayoutId is { } id) → 해당 도면으로 전환.
//   Changed 가 true 이면(전환하지 않아도) 호출자가 도면 목록을 다시 읽을 수 있다.
// [생성자 매개변수]
//   versions       : 도면/버전 저장소(VersionRepository). 목록 조회·생성·이름변경·삭제를 수행.
//   activeLayoutId : 지금 작업 중인 도면 ID(목록에서 ● 표시, 삭제 금지, "열기"해도 전환 없음).
//   context        : 변경 이력(감사)에 기록할 사용자/세션 정보(ChangeContext).
//   canChange      : 도면 편집 권한 여부. false 면 Rename/Delete 버튼 비활성, New 는 안내 후 거부.
// [흐름]
//   열기 → Loaded 시 ReloadAsync (도면 목록 조회, 각 도면의 기준 버전 이름을 "vN 라벨"로 해석,
//          ListView 에 Row 로 표시, 현재 도면(없으면 첫 행) 선택)
//   사용자 동작
//     · 목록 선택 변경(LayoutList_SelectionChanged) → Open 버튼 활성/비활성
//     · 더블클릭(LayoutList_DoubleClick) 또는 Open 버튼(Open_Click)
//         → 선택 행이 현재 도면이 아니면 SwitchToLayoutId 설정 → DialogResult = true
//     · New empty drawing...(New_Click)  → 권한 확인 → 이름 입력(PromptWindow) → CreateLayoutAsync → Changed = true → 재조회
//     · Rename...(Rename_Click)          → 이름 입력 → RenameLayoutAsync → Changed = true → 재조회
//     · Delete...(Delete_Click)          → 현재 도면이면 거부 → 확인 대화상자 → DeleteLayoutAsync
//                                          (마지막 남은 도면이면 서버가 거부) → Changed = true → 재조회
//     · Close(IsCancel)                  → 닫기(SwitchToLayoutId 는 null)
// ============================================================================
namespace SpatialEditor.App;

/// <summary>서버의 도면(레이아웃) 목록을 보여 주고 사용자가 도면을 전환/신규 생성/이름 변경/삭제하게 하는 창.</summary>
public partial class LayoutManagerWindow : Window
{
    /// <summary>ListView 한 줄에 표시할 도면 정보(화면 바인딩용 읽기 전용 레코드).</summary>
    /// <param name="Layout">원본 도면 정보.</param>
    /// <param name="Marker">현재 작업 중인 도면이면 "●", 아니면 빈 문자열.</param>
    /// <param name="Name">도면 이름.</param>
    /// <param name="ObjectCount">도면에 포함된 객체 수.</param>
    /// <param name="Origin">기준(분기 원본) 버전 표시("v번호 라벨"), 없으면 "-".</param>
    /// <param name="Created">생성 시각(로컬 시간, yyyy-MM-dd HH:mm).</param>
    private sealed record Row(LayoutInfo Layout, string Marker, string Name, int ObjectCount, string Origin, string Created);

    /// <summary>도면/버전 저장소(서버 DB 접근).</summary>
    private readonly VersionRepository versions;
    /// <summary>현재 작업 중인 도면 ID.</summary>
    private readonly long activeLayoutId;
    /// <summary>변경 이력에 남길 사용자/세션 정보.</summary>
    private readonly ChangeContext context;
    /// <summary>도면 생성/이름변경/삭제 권한 여부.</summary>
    private readonly bool canChange;

    /// <summary>사용자가 열기로 선택한 도면 ID. 현재 도면을 고르거나 그냥 닫았으면 null.</summary>
    public long? SwitchToLayoutId { get; private set; }

    /// <summary>이 창에서 도면을 만들거나 이름을 바꾸거나 삭제했으면 true(호출자가 목록 갱신 필요 여부 판단).</summary>
    public bool Changed { get; private set; }

    /// <summary>도면 관리 창을 만든다.</summary>
    /// <param name="versions">도면/버전 저장소.</param>
    /// <param name="activeLayoutId">현재 작업 중인 도면 ID.</param>
    /// <param name="context">변경 이력(감사) 컨텍스트.</param>
    /// <param name="canChange">편집 권한 여부(false 면 이름변경/삭제 버튼 비활성).</param>
    public LayoutManagerWindow(VersionRepository versions, long activeLayoutId, ChangeContext context, bool canChange)
    {
        InitializeComponent();
        // 1) 전달받은 의존 객체/상태를 보관하고, 권한이 없으면 Rename/Delete 를 비활성화한다.
        this.versions = versions;
        this.activeLayoutId = activeLayoutId;
        this.context = context;
        this.canChange = canChange;
        RenameButton.IsEnabled = DeleteButton.IsEnabled = canChange;
        // 2) 창이 표시된 뒤(Loaded) 비동기로 도면 목록을 불러온다(생성자에서 await 할 수 없으므로).
        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>ListView 에서 현재 선택된 행(없으면 null).</summary>
    private Row? Selected => LayoutList.SelectedItem as Row;

    /// <summary>서버에서 도면 목록을 다시 읽어 ListView 를 채우고 현재 도면(없으면 첫 행)을 선택한다. 오류는 메시지로 보여 준다.</summary>
    private async Task ReloadAsync()
    {
        try
        {
            // 1) 도면 전체 목록을 조회한다.
            var layouts = await versions.ListLayoutsAsync();
            var rows = new List<Row>();
            foreach (var layout in layouts)
            {
                // 2) 각 도면의 기준 버전이 있으면 "v번호 라벨" 로 풀어 쓰고, 현재 도면에는 ● 표시를 단다.
                string origin = "-";
                if (layout.BaseVersionId is { } baseId && await versions.GetVersionAsync(baseId) is { } baseVersion)
                {
                    origin = $"v{baseVersion.VersionNo} {baseVersion.Label}";
                }

                rows.Add(new Row(layout, layout.Id == activeLayoutId ? "●" : string.Empty, layout.Name, layout.ObjectCount, origin,
                    layout.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
            }

            // 3) 목록을 바인딩하고 현재 도면 행을 선택한다.
            LayoutList.ItemsSource = rows;
            LayoutList.SelectedItem = rows.FirstOrDefault(row => row.Layout.Id == activeLayoutId) ?? rows.FirstOrDefault();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Drawings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>LayoutList 의 SelectionChanged 핸들러. 선택된 행이 있을 때만 Open 버튼을 활성화한다.</summary>
    /// <param name="sender">도면 ListView.</param>
    /// <param name="e">선택 변경 인자.</param>
    private void LayoutList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OpenButton.IsEnabled = Selected is not null;

    /// <summary>LayoutList 의 MouseDoubleClick 핸들러. Open 버튼과 동일하게 동작한다.</summary>
    /// <param name="sender">도면 ListView.</param>
    /// <param name="e">마우스 버튼 인자.</param>
    private void LayoutList_DoubleClick(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

    /// <summary>[Open] 버튼 Click 핸들러. 선택한 도면을 열기로 확정하고 대화상자를 true 로 닫는다.</summary>
    /// <param name="sender">Open 버튼(또는 더블클릭에서 전달된 ListView).</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        // 이미 작업 중인 도면이면 전환할 필요가 없으므로 null, 다른 도면이면 그 ID 를 호출자에게 알린다.
        SwitchToLayoutId = row.Layout.Id == activeLayoutId ? null : row.Layout.Id;
        DialogResult = true;
    }

    /// <summary>[New empty drawing...] 버튼 Click 핸들러. 이름을 입력받아 빈 도면을 만든다.</summary>
    /// <param name="sender">New 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void New_Click(object sender, RoutedEventArgs e)
    {
        // 1) 권한 확인 → 2) 이름 입력(빈 값/취소면 중단) → 3) 서버에 생성 후 목록 갱신.
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

    /// <summary>[Rename...] 버튼 Click 핸들러. 선택한 도면의 이름을 바꾼다(설명은 유지).</summary>
    /// <param name="sender">Rename 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        // 현재 이름을 기본값으로 입력받고, 비었거나 같은 이름이면 아무 것도 하지 않는다.
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

    /// <summary>[Delete...] 버튼 Click 핸들러. 확인 후 선택한 도면(작업본과 모든 버전 포함)을 삭제한다.</summary>
    /// <param name="sender">Delete 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        // 1) 지금 작업 중인 도면은 삭제할 수 없다(다른 도면을 먼저 열어야 함).
        if (row.Layout.Id == activeLayoutId)
        {
            MessageBox.Show(this, "Open another drawing first. The drawing you are working in cannot be deleted.", "Drawings",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 2) 되돌릴 수 없으므로 기본 버튼이 No 인 확인 창을 띄운다.
        var answer = MessageBox.Show(this,
            $"Delete the drawing \"{row.Name}\" with its working copy and ALL of its versions? This cannot be undone.",
            "Delete drawing", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            // 3) 삭제 실행. 서버가 false 를 반환하면 마지막 남은 도면이라 삭제 불가.
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
