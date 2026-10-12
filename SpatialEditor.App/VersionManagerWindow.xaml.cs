using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SpatialEditor.Cad;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

// ============================================================================
// VersionManagerWindow : 한 도면의 버전 목록 관리 창 (저장/비교/복원/분기/기준선/보관/이름변경/삭제/DXF 내보내기)
// ----------------------------------------------------------------------------
// [호출 위치] MainWindow.Versions.cs VersionManager_Click (메뉴 Version > Version manager..., 도구모음 VersionManagerButton)
//   var window = new VersionManagerWindow(versions, activeLayoutId, activeLayoutName, CurrentChangeContext,
//                    isEditMode, Has(Permissions.DrawingEdit), Has(Permissions.DrawingExport), blocked) { Owner = this };
//   window.ShowDialog();  → 닫힌 뒤 호출자가 window.Outcome 으로 후속 처리:
//     Restored     → 복원 감사 기록 + 지도 다시 읽기
//     SwitchLayout → window.SwitchToLayoutId 도면으로 전환
//     ShowDiff     → window.ShownDiff / ShownDiffTitle 을 2D 화면에 겹쳐 그림
//     VersionsChanged/None → 상태 표시줄만 갱신
// [생성자 매개변수]
//   versions          : 도면/버전 저장소(VersionRepository).
//   layoutId          : 대상 도면 ID.
//   layoutName        : 대상 도면 이름(헤더 표시, 저장 창/분기 기본 이름/내보내기 파일명).
//   context           : 변경 이력(감사) 컨텍스트.
//   isEditing         : 메인 창이 편집 모드인지. 편집 중에는 복원 금지(먼저 저장/취소 요구).
//   canEdit           : 도면 편집 권한. 저장/복원/분기/기준선/보관/이름변경/삭제 버튼의 활성 조건.
//   canExport         : 내보내기 권한(Export as DXF 버튼 활성 조건).
//   saveBlockedReason : null 이 아니면 새 버전 저장을 막는 사유(예: 저장되지 않은 편집이 있음). 안내 후 저장 중단.
// [화면 구성] 상단: 헤더 + "자동 버전 표시" 체크(ShowAutoCheck) / 버튼 줄 / 버전 목록(VersionList, 다중 선택)
//            하단: 비교 결과(DiffList) + "Show in 2D view"(ShowInViewButton) / "Export CSV…"(CsvButton) / 메시지(MessageText)
// [흐름]
//   열기 → 생성자: 값 보관, 헤더 설정 → Loaded 시 loaded = true 후 ReloadAsync (버전 목록 조회/표시)
//   사용자 동작
//     · 선택 변경(VersionList_SelectionChanged) → UpdateButtons (선택 개수/권한/상태에 따라 버튼 활성화)
//     · 더블클릭(VersionList_DoubleClick)       → 비교 가능하면 Compare_Click
//     · 자동 버전 표시 토글(ShowAuto_Changed)    → 목록 다시 조회
//     · Save new version…(Save_Click)  → 저장 차단 사유 확인 → SaveVersionWindow → CreateVersionAsync(수동)
//         (직전 버전과 동일하면 null 반환 → 만들지 않음) → Outcome = VersionsChanged
//     · Rename…(Edit_Click)            → SaveVersionWindow(이름 변경 모드) → UpdateVersionAsync
//     · Set/release baseline(Baseline_Click) → 기준선 지정/해제(SetStatusAsync)
//     · Archive/unarchive(Archive_Click)     → 보관/해제 토글
//     · Delete(Delete_Click)           → 확인 → DeleteVersionAsync → Outcome = VersionsChanged
//     · Restore(Restore_Click)         → 편집 중이면 거부 → 확인 → RestoreVersionAsync(필요 시 현재 상태를 자동 버전으로 보존)
//                                         → Outcome = Restored → DialogResult = true (창 닫힘)
//     · Branch to new drawing…(Branch_Click) → 이름 입력 → CreateLayoutFromVersionAsync → 열기 여부 확인
//                                         → 예: Outcome = SwitchLayout, SwitchToLayoutId 설정, DialogResult = true
//     · Compare(Compare_Click)         → 1개 선택: 작업본과 비교 / 2개 선택: 두 버전 비교 → DiffAsync → DiffList 표시
//     · Show in 2D view(ShowInView_Click) → Outcome = ShowDiff, DialogResult = true (메인 화면에 비교 결과 표시)
//     · Export CSV…(Csv_Click)         → 비교 결과를 CSV(UTF-8 BOM)로 저장
//     · Export as DXF…(Export_Click)   → 선택 버전의 블록을 DXF 로 저장(DxfBlockExporter)
//   Close(XAML 의 닫기) → 창 닫힘(Outcome 에 따라 호출자가 후속 처리).
// ============================================================================
namespace SpatialEditor.App;

/// <summary>버전 관리 창이 닫힐 때 메인 창이 해야 할 후속 작업을 알리는 결과 값.</summary>
public enum VersionManagerOutcome
{
    /// <summary>메인 창이 추가로 할 일이 없음.</summary>
    None,
    /// <summary>작업본이 버전으로 교체되었으므로 지도를 다시 읽어야 함.</summary>
    Restored,
    /// <summary>버전이 추가/변경/삭제되었으므로 상태 표시줄 갱신이 필요함.</summary>
    VersionsChanged,
    /// <summary>다른 도면을 열어야 함(분기로 새 도면을 만들고 바로 열기로 선택한 경우).</summary>
    SwitchLayout,
    /// <summary>2D 화면 위에 <see cref="VersionManagerWindow.ShownDiff"/> 비교 결과를 겹쳐 그려야 함.</summary>
    ShowDiff
}

/// <summary>
/// 한 도면의 버전 목록 창: 새 버전 저장, 두 버전(또는 버전과 작업본) 비교, 복원, 새 도면으로 분기,
/// 기준선 지정, 보관, 이름 변경, 삭제, DXF 내보내기. 메인 창이 보는 내용을 바꾸는 동작은
/// <see cref="Outcome"/> 을 설정하고 대화상자를 닫는 방식으로 끝난다.
/// </summary>
public partial class VersionManagerWindow : Window
{
    /// <summary>버전 목록(VersionList) 한 줄(바인딩용). 원본 버전과 화면에 표시할 문자열들.</summary>
    /// <param name="Version">원본 버전 정보.</param>
    /// <param name="No">"v번호" 표시.</param>
    /// <param name="Label">버전 이름.</param>
    /// <param name="KindText">종류(auto / pre-restore / manual).</param>
    /// <param name="StatusText">상태(BASELINE / released / archived / draft).</param>
    /// <param name="Author">작성자 이름(없으면 "-").</param>
    /// <param name="Time">작성 시각(로컬 yyyy-MM-dd HH:mm).</param>
    /// <param name="BlockCount">설비 블록 수.</param>
    /// <param name="FeatureCount">그 밖의 객체 수.</param>
    /// <param name="Parent">부모 버전 번호("vN", 없으면 "-").</param>
    /// <param name="Comment">설명(줄바꿈은 공백으로 치환).</param>
    private sealed record VersionRow(
        LayoutVersion Version, string No, string Label, string KindText, string StatusText, string Author, string Time,
        int BlockCount, int FeatureCount, string Parent, string Comment);

    /// <summary>비교 결과 목록(DiffList) 한 줄(바인딩용).</summary>
    /// <param name="Entry">원본 차이 항목.</param>
    /// <param name="KindText">변경 종류 문구(added/removed/moved/...).</param>
    /// <param name="Name">객체 이름.</param>
    /// <param name="Layer">레이어 이름(없으면 "-").</param>
    /// <param name="Before">변경 전 값.</param>
    /// <param name="After">변경 후 값.</param>
    private sealed record DiffRow(DiffEntry Entry, string KindText, string Name, string Layer, string Before, string After);

    /// <summary>도면/버전 저장소.</summary>
    private readonly VersionRepository versions;
    /// <summary>대상 도면 ID.</summary>
    private readonly long layoutId;
    /// <summary>대상 도면 이름.</summary>
    private readonly string layoutName;
    /// <summary>변경 이력(감사)에 기록할 사용자/세션 정보.</summary>
    private readonly ChangeContext context;
    /// <summary>메인 창이 편집 모드이면 true(복원 금지 조건).</summary>
    private readonly bool isEditing;
    /// <summary>도면 편집 권한 보유 여부.</summary>
    private readonly bool canEdit;
    /// <summary>내보내기 권한 보유 여부.</summary>
    private readonly bool canExport;
    /// <summary>null 이 아니면 새 버전 저장을 막는 사유 문구.</summary>
    private readonly string? saveBlockedReason;
    /// <summary>현재 목록에 표시된 버전을 ID 로 찾기 위한 사전.</summary>
    private readonly Dictionary<long, LayoutVersion> byId = new();
    /// <summary>창이 Loaded 된 뒤에만 true. 초기화 중 컨트롤 이벤트로 목록을 읽거나 버튼을 갱신하는 것을 막는다.</summary>
    private bool loaded;

    /// <summary>창이 닫힌 뒤 메인 창이 수행할 후속 작업(기본 None).</summary>
    public VersionManagerOutcome Outcome { get; private set; }
    /// <summary>Outcome 이 SwitchLayout 일 때 열어야 할 도면 ID.</summary>
    public long? SwitchToLayoutId { get; private set; }
    /// <summary>가장 최근 비교 결과(Show in 2D view 와 CSV 내보내기에 사용). 비교 전에는 null.</summary>
    public VersionDiff? ShownDiff { get; private set; }
    /// <summary>비교 결과의 제목(예: "v2 → working copy", "v1 → v3").</summary>
    public string ShownDiffTitle { get; private set; } = string.Empty;

    /// <summary>버전 관리 창을 만든다.</summary>
    /// <param name="versions">도면/버전 저장소.</param>
    /// <param name="layoutId">대상 도면 ID.</param>
    /// <param name="layoutName">대상 도면 이름.</param>
    /// <param name="context">변경 이력(감사) 컨텍스트.</param>
    /// <param name="isEditing">메인 창이 편집 모드인지(true 면 복원 불가).</param>
    /// <param name="canEdit">도면 편집 권한 여부.</param>
    /// <param name="canExport">내보내기 권한 여부.</param>
    /// <param name="saveBlockedReason">새 버전 저장을 막는 사유(없으면 null).</param>
    public VersionManagerWindow(
        VersionRepository versions, long layoutId, string layoutName, ChangeContext context,
        bool isEditing, bool canEdit, bool canExport, string? saveBlockedReason)
    {
        InitializeComponent();
        // 1) 전달받은 값을 보관하고 헤더에 도면 이름을 표시한다.
        this.versions = versions;
        this.layoutId = layoutId;
        this.layoutName = layoutName;
        this.context = context;
        this.isEditing = isEditing;
        this.canEdit = canEdit;
        this.canExport = canExport;
        this.saveBlockedReason = saveBlockedReason;
        HeaderText.Text = $"Drawing: {layoutName}";
        // 2) 창이 표시된 뒤 loaded 플래그를 세우고 비동기로 버전 목록을 읽는다.
        Loaded += async (_, _) =>
        {
            loaded = true;
            await ReloadAsync();
        };
    }

    // ---------------------------------- 목록 ----------------------------------

    /// <summary>버전 종류 코드를 화면 문구로 바꾼다.</summary>
    /// <param name="kind">VersionKind 값(Auto/PreRestore/그 외 수동).</param>
    /// <returns>"auto", "pre-restore", "manual" 중 하나.</returns>
    private static string KindText(string kind) => kind switch
    {
        VersionKind.Auto => "auto",
        VersionKind.PreRestore => "pre-restore",
        _ => "manual"
    };

    /// <summary>버전 상태 코드를 화면 문구로 바꾼다.</summary>
    /// <param name="status">VersionStatus 값.</param>
    /// <returns>"BASELINE", "released", "archived", "draft" 중 하나.</returns>
    private static string StatusText(string status) => status switch
    {
        VersionStatus.Baseline => "BASELINE",
        VersionStatus.Released => "released",
        VersionStatus.Archived => "archived",
        _ => "draft"
    };

    /// <summary>서버에서 버전 목록을 다시 읽어 표시한다(자동 버전 표시 여부 반영). 선택을 복원하고 버튼 상태를 갱신한다.</summary>
    /// <param name="select">다시 읽은 뒤 선택할 버전 ID(없으면 선택 없음).</param>
    private async Task ReloadAsync(long? select = null)
    {
        try
        {
            // 1) 목록을 조회하고 ID 사전을 다시 만든다.
            var all = await versions.ListVersionsAsync(layoutId, ShowAutoCheck.IsChecked == true);
            byId.Clear();
            foreach (var version in all)
            {
                byId[version.Id] = version;
            }

            // 2) "From" 열은 부모 버전이 필터로 숨겨져 있어도 번호를 보여 주도록, 자동 버전을 포함한 전체 목록에서 번호를 찾는다.
            var numbers = (await versions.ListVersionsAsync(layoutId, true)).ToDictionary(version => version.Id, version => version.VersionNo);
            var rows = all.Select(version => new VersionRow(
                version, $"v{version.VersionNo}", version.Label, KindText(version.Kind), StatusText(version.Status),
                version.CreatedByName ?? "-", version.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                version.BlockCount, version.FeatureCount,
                version.ParentVersionId is { } parent && numbers.TryGetValue(parent, out var no) ? $"v{no}" : "-",
                (version.Comment ?? string.Empty).Replace('\n', ' '))).ToList();
            // 3) 표시하고, 요청된 버전이 있으면 선택한 뒤 안내 문구/버튼 상태를 갱신한다.
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

    /// <summary>목록에서 선택된 모든 행(다중 선택 가능).</summary>
    private List<VersionRow> SelectedRows => VersionList.SelectedItems.OfType<VersionRow>().ToList();

    /// <summary>정확히 한 행만 선택되었을 때 그 버전, 아니면 null.</summary>
    private LayoutVersion? SingleSelected => SelectedRows is { Count: 1 } rows ? rows[0].Version : null;

    /// <summary>선택 개수, 권한, 편집 모드, 기준선 여부에 따라 각 버튼의 활성/비활성을 정한다. 기준선 버전은 이름 변경/삭제/보관이 막힌다.</summary>
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

    /// <summary>VersionList 의 SelectionChanged 핸들러. 버튼 상태를 갱신한다.</summary>
    /// <param name="sender">버전 ListView.</param>
    /// <param name="e">선택 변경 인자.</param>
    private void VersionList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();

    /// <summary>VersionList 의 MouseDoubleClick 핸들러. 비교 가능한 선택이면 Compare 와 동일하게 동작한다.</summary>
    /// <param name="sender">버전 ListView.</param>
    /// <param name="e">마우스 버튼 인자.</param>
    private void VersionList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CompareButton.IsEnabled)
        {
            Compare_Click(sender, e);
        }
    }

    /// <summary>ShowAutoCheck 의 Checked/Unchecked 핸들러. 자동 버전 표시 여부가 바뀌면 목록을 다시 읽는다.</summary>
    /// <param name="sender">"Show automatic versions" 체크박스.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void ShowAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (loaded)
        {
            await ReloadAsync();
        }
    }

    // ------------------------------ 저장 / 수정 ------------------------------

    /// <summary>[Save new version…] 버튼 Click 핸들러. 이름/설명을 입력받아 현재 저장된 도면을 수동 버전으로 저장한다.</summary>
    /// <param name="sender">Save 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        // 1) 저장되지 않은 편집 등 저장 불가 사유가 있으면 안내하고 중단한다(버전은 저장된 도면만 담기 때문).
        if (saveBlockedReason is not null)
        {
            MessageBox.Show(this, saveBlockedReason, "Save version", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 2) 이름/설명 입력(기본 이름: 도면명 + 현재 시각).
        var dialog = new SaveVersionWindow(layoutName, $"{layoutName} {DateTime.Now:MM-dd HH:mm}") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // 3) 저장. 직전 버전과 내용이 같으면 null 이 반환되어 새 버전을 만들지 않는다.
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

    /// <summary>[Rename…] 버튼 Click 핸들러. 선택한 버전의 이름과 설명을 수정한다.</summary>
    /// <param name="sender">Rename 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    /// <summary>[Set / release baseline] 버튼 Click 핸들러. 선택한 버전을 기준선으로 지정하거나(확인 후), 이미 기준선이면 released 로 되돌린다.</summary>
    /// <param name="sender">Baseline 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    /// <summary>[Archive / unarchive] 버튼 Click 핸들러. 선택한 버전을 보관 처리하거나 보관 상태이면 draft 로 되돌린다.</summary>
    /// <param name="sender">Archive 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    /// <summary>[Delete] 버튼 Click 핸들러. 확인 후 선택한 버전을 삭제한다(이력 연결은 유지되어 후속 버전은 선행 버전에 붙는다).</summary>
    /// <param name="sender">Delete 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    // ---------------------------- 복원 / 분기 ----------------------------

    /// <summary>[Restore] 버튼 Click 핸들러. 선택한 버전을 현재 도면의 작업본으로 복원한다. 성공하면 대화상자를 닫는다.</summary>
    /// <param name="sender">Restore 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected is not { } version)
        {
            return;
        }

        // 1) 편집 중에는 복원할 수 없다(저장/취소 후 다시 시도).
        if (isEditing)
        {
            MessageBox.Show(this, "Leave edit mode (Save or Cancel) before restoring a version.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 2) 사용자 확인.
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
            // 3) 복원 실행. 현재 상태가 최신 버전과 다르면 서버가 먼저 자동 버전(safety)으로 보존하므로 되돌릴 수 있다.
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

    /// <summary>[Branch to new drawing…] 버튼 Click 핸들러. 선택한 버전의 복사본으로 새 도면을 만들고, 원하면 바로 열도록 요청한다.</summary>
    /// <param name="sender">Branch 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    // ------------------------------- 비교 -------------------------------

    /// <summary>[Compare] 버튼 Click 핸들러(더블클릭에서도 호출). 선택이 1개면 작업본과, 2개면 두 버전끼리 비교해 DiffList 에 표시한다.</summary>
    /// <param name="sender">Compare 버튼 또는 VersionList.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        // 1) 선택이 1개 또는 2개일 때만 비교한다.
        var rows = SelectedRows;
        if (rows.Count is not (1 or 2))
        {
            MessageBox.Show(this, "Select one version (to compare with the working copy) or two versions.", "Compare", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 2) 비교 기준(from)과 대상(to)을 정한다: 2개면 번호순(오래된 것 → 새 것), 1개면 to = null(작업본).
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
            // 3) 차이를 계산하고 목록/요약/버튼 상태를 갱신한다.
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

    /// <summary>차이 종류를 화면/CSV 문구로 바꾼다.</summary>
    /// <param name="kind">차이 종류.</param>
    /// <returns>added / removed / moved / rotated / replaced / attributes 중 하나.</returns>
    internal static string KindLabel(DiffKind kind) => kind switch
    {
        DiffKind.Added => "added",
        DiffKind.Removed => "removed",
        DiffKind.Moved => "moved",
        DiffKind.Rotated => "rotated",
        DiffKind.Reshaped => "replaced",
        _ => "attributes"
    };

    /// <summary>비교 결과를 "added 3 · moved 1" 처럼 종류별 개수로 요약한다.</summary>
    /// <param name="diff">비교 결과.</param>
    /// <returns>차이가 없으면 "identical", 있으면 개수가 0 보다 큰 종류만 이은 문자열.</returns>
    internal static string Summary(VersionDiff diff) =>
        diff.IsEmpty
            ? "identical"
            : string.Join(" · ", new[]
            {
                (DiffKind.Added, "added"), (DiffKind.Removed, "removed"), (DiffKind.Moved, "moved"),
                (DiffKind.Rotated, "rotated"), (DiffKind.Reshaped, "replaced"), (DiffKind.AttributesChanged, "attributes")
            }.Where(pair => diff.Count(pair.Item1) > 0).Select(pair => $"{pair.Item2} {diff.Count(pair.Item1)}"));

    /// <summary>[Show in 2D view] 버튼 Click 핸들러. 비교 결과를 메인 2D 화면에 표시하도록 Outcome 을 설정하고 대화상자를 닫는다.</summary>
    /// <param name="sender">Show in 2D view 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void ShowInView_Click(object sender, RoutedEventArgs e)
    {
        if (ShownDiff is null || ShownDiff.IsEmpty)
        {
            return;
        }

        Outcome = VersionManagerOutcome.ShowDiff;
        DialogResult = true;
    }

    /// <summary>[Export CSV…] 버튼 Click 핸들러. 비교 결과를 CSV 파일(엑셀에서 한글이 깨지지 않도록 UTF-8 BOM)로 저장한다.</summary>
    /// <param name="sender">Export CSV 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Csv_Click(object sender, RoutedEventArgs e)
    {
        if (ShownDiff is null)
        {
            return;
        }

        // 1) 저장 위치 선택.
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"diff_{DateTime.Now:yyyyMMdd_HHmmss}.csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // 2) 로컬 함수: 각 값을 따옴표로 감싸고 내부 따옴표는 두 번 써서 CSV 규칙에 맞춘다.
        string Cell(string? text) => "\"" + (text ?? string.Empty).Replace("\"", "\"\"") + "\"";
        var csv = new StringBuilder();
        csv.AppendLine("change,object,layer,before,after");
        foreach (var entry in ShownDiff.Entries)
        {
            csv.AppendLine(string.Join(",", Cell(KindLabel(entry.Kind)), Cell(entry.Name), Cell(entry.LayerName), Cell(entry.Before), Cell(entry.After)));
        }

        // 3) UTF-8(BOM 포함)로 파일에 쓰고 결과를 메시지 영역에 알린다.
        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
        MessageText.Text = $"Wrote {ShownDiff.Entries.Count} row(s) to {dialog.FileName}";
    }

    // ------------------------------- 내보내기 -------------------------------

    /// <summary>[Export as DXF…] 버튼 Click 핸들러. 선택한 버전의 설비 블록을 DXF 파일로 내보낸다.</summary>
    /// <param name="sender">Export 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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
            // 삽입점이 있는 블록만 내보낸다. 파일 쓰기는 UI 가 멈추지 않도록 백그라운드(Task.Run)에서 수행한다.
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
