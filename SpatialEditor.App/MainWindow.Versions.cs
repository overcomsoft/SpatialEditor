using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

// ============================================================================
// MainWindow.Versions.cs : partial class MainWindow 중 "도면(레이아웃)/버전 관리 + 비교 오버레이" 담당 부분
// ----------------------------------------------------------------------------
// 흐름도
//  [로그인 직후] EnsureDrawingSchemasAsync: 장비 DB 스키마(PostGisFeatureRepository.EnsureSchemaAsync)
//     → 버전 DB 스키마(VersionRepository.EnsureSchemaAsync) 를 읽기 전에 보장(구 DB 자동 업그레이드)
//  [상태 표시] RefreshLayoutStatusAsync: GetStatusAsync(활성 도면) → 삭제됐으면 기본 도면(1)로 대체
//     → "도면: 이름 · vN 기반 (수정됨)" 문구/툴팁 → 상태 영역 클릭 시 버전 관리자
//  [버전 저장]
//     수동: SaveVersion_Click → 권한/미저장 편집 확인 → SaveVersionWindow(이름·메모)
//           → CreateVersionAsync(Manual) → 변경 없으면 미생성 안내 → 감사 로그(version.save) → 상태 갱신
//     자동: CreateAutoVersionAsync(편집 저장 후, 설정 켜짐일 때) → CreateVersionAsync(Auto), 실패해도 저장은 막지 않음
//  [버전 관리자] VersionManager_Click → VersionManagerWindow → 결과(Outcome)에 따라
//     Restored → 감사 로그 + ReloadForSyncAsync 로 다시 로드
//     SwitchLayout → SwitchLayoutAsync   /   ShowDiff → ShowDiffOverlay
//  [도면 전환] LayoutManager_Click → LayoutManagerWindow → SwitchLayoutAsync:
//     편집 모드면 거절 → 활성 도면 변경, 필터/오버레이/지도 초기화 → StartSyncAsync → LoadImportedPolygonsAsync → 상태 갱신
//  [비교 오버레이] ShowDiffOverlay(diff, title)
//     기존 오버레이 제거 → 항목별 종류(추가/삭제/이동/회전/모양교체/속성)에 맞춰 AddDiffShape/AddDiffArrow
//     → RegisterDiffShape(확대/축소에 맞는 선 두께, Z 순서) → 범례(Border) 추가 → 상태줄 요약
//     해제: ClearDiffOverlay (비교 버튼 토글/Esc/메뉴)
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// MainWindow 의 도면(레이아웃)과 버전 관리 부분: 활성 도면, 버전 저장, 버전 관리자, 도면 전환,
/// 그리고 2D 화면에 그려지는 비교(차이) 오버레이.
/// </summary>
public partial class MainWindow
{
    /// <summary>현재 열려 있는 도면(레이아웃)의 DB ID. 기본 1(기본 도면).</summary>
    private long activeLayoutId = 1;
    /// <summary>현재 열려 있는 도면의 이름(표시/감사 로그용). 기본 "기본 도면".</summary>
    private string activeLayoutName = "기본 도면";

    /// <summary>현재 연결 설정으로 버전 저장소를 새로 만듭니다. 호출자가 <c>await using</c> 으로 해제해야 합니다.</summary>
    /// <returns>새 <see cref="VersionRepository"/>.</returns>
    private VersionRepository NewVersionRepository() => new(connectionSettings.ConnectionString);

    /// <summary>
    /// 로그인 직후, 무언가를 읽기 전에 DB 를 현재 스키마(도면, 객체 ID, 버전)로 올립니다. 구버전 DB 는 이 버전이
    /// 처음 접속할 때 제자리에서 업그레이드됩니다.
    /// </summary>
    /// <returns>준비에 성공하면 true. 실패하면 상태줄과 메시지 상자로 알리고 false.</returns>
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

    /// <summary>
    /// 상태 영역의 도면 문구("도면: 이름 · vN 기반 (수정됨)")와 툴팁을 DB 의 최신 상태로 갱신합니다.
    /// 연결/로그인 전이거나 조회에 실패하면 문구를 비우며, 예외를 밖으로 던지지 않습니다.
    /// </summary>
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
                // 다른 사용자가 이 도면을 삭제했으면 기본 도면(ID 1)으로 되돌려 다시 조회합니다.
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

    /// <summary>상태 영역의 도면 문구를 클릭하면 버전 관리자를 엽니다.</summary>
    /// <param name="sender">클릭된 요소.</param>
    /// <param name="e">마우스 버튼 이벤트 인수.</param>
    private void LayoutStatus_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => VersionManager_Click(sender, e);

    // ---------------------------------------------------------------- versions

    /// <summary>편집 저장마다 자동 버전을 남깁니다(설정에서 켠 경우). 저장 자체를 막거나 실패시키지 않으며, 마지막에 도면 상태 문구를 갱신합니다.</summary>
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

    /// <summary>"버전 저장" 명령: 이름/메모를 받아 현재 저장된 도면을 수동 버전으로 기록합니다.</summary>
    /// <param name="sender">이벤트를 일으킨 메뉴/버튼.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private async void SaveVersion_Click(object sender, RoutedEventArgs e)
    {
        // 1) 편집 권한과 연결 상태를 확인합니다.
        if (!Require(Permissions.DrawingEdit, "save versions") || !isConnected)
        {
            return;
        }

        // 2) 버전은 저장된 도면을 보관하므로, 미저장 편집이 있으면 먼저 저장하도록 안내하고 중단합니다.
        if (isEditMode && HasPendingEdits())
        {
            MessageBox.Show(this, "Save your edits first (Ctrl+S). A version stores the saved drawing, not unsaved edits.", "Save version",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 3) 이름·메모 입력 창(기본 이름: 도면 이름 + 날짜시각). 취소하면 종료.
        var dialog = new SaveVersionWindow(activeLayoutName, $"{activeLayoutName} {DateTime.Now:MM-dd HH:mm}") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // 4) 버전 생성. 직전 버전 이후 변경이 없으면 null 이 반환되어 새 버전이 만들어지지 않습니다.
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

    /// <summary>"버전 관리자" 창을 열고, 닫힌 결과(복원/도면 전환/차이 표시)에 맞게 후속 처리를 합니다.</summary>
    /// <param name="sender">이벤트를 일으킨 메뉴/버튼.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private async void VersionManager_Click(object sender, RoutedEventArgs e)
    {
        if (!isConnected || !Require(Permissions.DrawingView, "view versions"))
        {
            return;
        }

        await using var versions = NewVersionRepository();
        // 미저장 편집이 있으면 버전 관리자에 차단 사유를 넘겨, 복원 등 위험한 동작을 막게 합니다.
        var blocked = isEditMode && HasPendingEdits() ? "Save your edits first (Ctrl+S). A version stores the saved drawing, not unsaved edits." : null;
        var window = new VersionManagerWindow(
            versions, activeLayoutId, activeLayoutName, CurrentChangeContext,
            isEditMode, Has(Permissions.DrawingEdit), Has(Permissions.DrawingExport), blocked) { Owner = this };
        window.ShowDialog();

        // 창이 알려준 결과에 따라 후속 동작: 복원 → 다시 로드, 도면 전환, 차이 오버레이 표시.
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

    /// <summary>"도면 관리" 창을 열고, 다른 도면이 선택되면 전환합니다. 전환이 아니면 상태 문구만 갱신합니다.</summary>
    /// <param name="sender">이벤트를 일으킨 메뉴/버튼.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
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

    /// <summary>다른 도면을 엽니다: 그 도면의 작업본이 화면 내용을 대체합니다. 편집 모드에서는 거절됩니다.</summary>
    /// <param name="layoutId">열 도면의 DB ID.</param>
    private async Task SwitchLayoutAsync(long layoutId)
    {
        if (isEditMode)
        {
            MessageBox.Show(this, "Leave edit mode (Save or Cancel) before opening another drawing.", "Drawings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 활성 도면 변경 → 레이어 필터/비교 오버레이/지도를 비우고, 동기화를 새 도면 기준으로 다시 시작해 불러옵니다.
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

    /// <summary>비교 오버레이로 지도 위에 올려진 도형들(해제 시 일괄 제거용).</summary>
    private readonly List<Shape> diffOverlayShapes = new();
    /// <summary>비교 오버레이의 범례 패널(없으면 null). 지도 캔버스(MapCanvas) 위에 고정 위치로 표시.</summary>
    private Border? diffLegend;

    /// <summary>비교 오버레이(도형 또는 범례)가 표시 중인지 여부.</summary>
    private bool HasDiffOverlay => diffOverlayShapes.Count > 0 || diffLegend is not null;

    /// <summary>"비교 해제" 명령: 비교 오버레이를 지웁니다.</summary>
    /// <param name="sender">이벤트를 일으킨 메뉴/버튼.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private void ClearComparison_Click(object sender, RoutedEventArgs e) => ClearDiffOverlay();

    /// <summary>도구 모음 "비교" 버튼(토글): 표시 중이면 해제하고, 아니면 버전 관리자를 열어 비교 대상을 고르게 합니다.</summary>
    /// <param name="sender">이벤트를 일으킨 버튼.</param>
    /// <param name="e">라우트 이벤트 인수(그대로 버전 관리자에 전달).</param>
    private void CompareToolbar_Click(object sender, RoutedEventArgs e)
    {
        if (HasDiffOverlay)
        {
            ClearDiffOverlay();
            return;
        }

        VersionManager_Click(sender, e);
    }

    /// <summary>비교 오버레이의 모든 도형과 범례를 지도에서 제거하고 관련 상태를 초기화합니다.</summary>
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

    /// <summary>차이 종류별 (종류, 범례 문구, 색) 표. 추가=초록, 삭제=빨강, 이동=주황, 회전=보라, 모양 교체=파랑, 속성=청록.</summary>
    private static readonly (DiffKind Kind, string Text, Color Color)[] DiffLegendItems =
    {
        (DiffKind.Added, "추가", Color.FromRgb(0x2E, 0x7D, 0x32)),
        (DiffKind.Removed, "삭제", Color.FromRgb(0xC6, 0x28, 0x28)),
        (DiffKind.Moved, "이동", Color.FromRgb(0xEF, 0x6C, 0x00)),
        (DiffKind.Rotated, "회전", Color.FromRgb(0x6A, 0x1B, 0x9A)),
        (DiffKind.Reshaped, "모양 교체", Color.FromRgb(0x15, 0x65, 0xC0)),
        (DiffKind.AttributesChanged, "속성", Color.FromRgb(0x00, 0x79, 0x6B))
    };

    /// <summary>차이 종류에 해당하는 표시 색을 범례 표에서 찾습니다.</summary>
    /// <param name="kind">차이 종류.</param>
    /// <returns>해당 종류의 색.</returns>
    private static Color DiffColor(DiffKind kind) => DiffLegendItems.First(item => item.Kind == kind).Color;

    /// <summary>
    /// 두 버전(또는 버전과 현재) 사이의 차이를 불러온 도면 위에 겹쳐 그리고, 범례와 요약을 표시합니다.
    /// 추가는 채움, 삭제는 점선, 이동/회전/모양 교체는 이전 모양(회색 점선)과 이후 모양(색)을 함께 그립니다.
    /// </summary>
    /// <param name="diff">표시할 차이 결과(항목 목록과 종류별 개수).</param>
    /// <param name="title">범례와 상태줄에 표시할 비교 제목.</param>
    private void ShowDiffOverlay(VersionDiff diff, string title)
    {
        // 1) 이전 비교 표시를 지우고, 화면에 불러온 도면이 없으면(그릴 기준이 없음) 안내 후 중단합니다.
        ClearDiffOverlay();
        if (renderedEnvelope is null)
        {
            StatusText.Text = "Load layers first; the comparison is drawn over the loaded drawing.";
            return;
        }

        // 2) 차이 항목마다 종류에 맞는 도형을 그립니다. 이동/회전/모양 교체는 '이전(회색 점선) → 이후(색)'을 함께 보여 줍니다.
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

        // 3) 범례 패널: 제목, 요약, 개수가 있는 종류만 색 견본과 함께 나열하고 해제 방법을 안내합니다.
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
        // 4) 범례는 마우스 입력을 가로채지 않도록(IsHitTestVisible=false) 왼쪽 위에 가장 위 Z 순서로 얹습니다.
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

    /// <summary>지오메트리를 오버레이 도형(Path)으로 그려 지도 레이어 위에 추가합니다.</summary>
    /// <param name="geometry">그릴 지오메트리(월드 좌표). null 이거나 비어 있으면 무시.</param>
    /// <param name="color">윤곽선 색(채움은 같은 색의 약 33% 투명도).</param>
    /// <param name="dashed">true 면 점선(삭제/이전 모양 표현).</param>
    /// <param name="fill">true 면 반투명 채움(추가 표현).</param>
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

    /// <summary>이동 전/후 형상의 중심을 잇는 화살표(선분) 오버레이를 추가합니다.</summary>
    /// <param name="from">이동 전 지오메트리. null 이거나 비어 있으면 무시.</param>
    /// <param name="to">이동 후 지오메트리. null 이거나 비어 있으면 무시.</param>
    /// <param name="color">선 색.</param>
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

    /// <summary>오버레이 도형을 등록합니다: 확대/축소에도 일정한 화면 두께가 되도록 기준 두께를 기록하고, 레이어 캔버스에 올립니다.</summary>
    /// <param name="shape">등록할 도형.</param>
    /// <param name="width">화면 기준 선 두께(픽셀). 현재 배율로 나눠 실제 두께를 정함.</param>
    private void RegisterDiffShape(Shape shape, double width)
    {
        baseStrokeWidths[shape] = width;
        shape.StrokeThickness = width / Math.Max(mapScale.ScaleX, 0.1);
        Panel.SetZIndex(shape, 99000);
        MapLayerCanvas.Children.Add(shape);
        diffOverlayShapes.Add(shape);
    }
}
