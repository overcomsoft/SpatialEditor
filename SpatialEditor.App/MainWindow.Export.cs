using System.IO;
using System.Windows;
using Microsoft.Win32;
using SpatialEditor.Cad;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

// ============================================================================
// MainWindow.Export.cs : partial class MainWindow 중 "DXF 내보내기 + 레이어 삭제" 담당 부분
// ----------------------------------------------------------------------------
// 흐름도 (DXF 내보내기: ExportDxf_Click)
//   권한 확인(DrawingExport) → 저장 대화상자(파일명 Equipment_yyyyMMdd_HHmmss.dxf)
//     → 바쁨 표시(ShowBusy)
//     → 편집 모드면 CollectEditedInstances (미저장 편집 상태 반영: 삭제 제외, 새 복제/그룹 포함)
//        아니면 LoadStoredInstancesAsync (DB 의 편집 가능 블록 인스턴스, 숨긴 레이어 제외)
//          → 각 객체를 ToExportInstance 로 변환(삽입점/회전/스케일 속성 → CadExportInstance)
//     → 0건이면 안내 후 종료 → Task.Run 으로 DxfBlockExporter.Export (UI 멈춤 방지)
//     → 상태줄 갱신 → 감사 로그(export.dxf) → 예외는 메시지 상자 → finally 바쁨 해제
// 흐름도 (레이어 삭제: SelectedLayerDelete_Click → DeleteLayerAsync)
//   선택 레이어 확인 → 권한(DrawingDelete)/다시 읽기 가능 여부 확인 → 확인 대화상자(기본 "아니오")
//     → DB 저장소(PostGisFeatureRepository).DeleteLayerAsync → 로그 기록
//     → 레이어 필터/숨김 목록에서 제거 → ReloadForSyncAsync 로 다시 로드 → 감사 로그(layer.delete)
// ============================================================================
namespace SpatialEditor.App;

/// <summary>MainWindow 의 내보내기/레이어 삭제 부분(partial class 의 일부).</summary>
public partial class MainWindow
{
    /// <summary>
    /// 장비 블록 객체를 DXF 로 내보냅니다: 블록 이름마다 BLOCK 1개, 객체마다 INSERT 1개이며
    /// 이름/회전/위치점이 블록 속성으로 기록됩니다. 편집 모드에서는 현재(미저장) 편집 상태를,
    /// 그렇지 않으면 저장된 객체를 내보냅니다.
    /// </summary>
    /// <param name="sender">이벤트를 일으킨 메뉴 항목.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private async void ExportDxf_Click(object sender, RoutedEventArgs e)
    {
        // 1) 권한 확인: 내보내기 권한이 없으면 중단합니다.
        if (!Require(Permissions.DrawingExport, "export DXF data"))
        {
            return;
        }

        // 2) 저장 위치 선택(기본 확장자 .dxf, 덮어쓰기 확인). 취소하면 종료합니다.
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
            // 3) 내보낼 인스턴스 수집: 편집 중이면 메모리의 편집 상태, 아니면 DB 저장본.
            ShowBusy("Exporting DXF...");
            var instances = isEditMode ? CollectEditedInstances() : await LoadStoredInstancesAsync();
            if (instances.Count == 0)
            {
                StatusText.Text = "Export skipped: no block objects to export.";
                MessageBox.Show(this, "There are no equipment block objects to export.", "Export DXF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 4) 파일 쓰기는 백그라운드 스레드에서 수행하고, 완료 후 상태줄과 감사 로그를 남깁니다.
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

    /// <summary>선택된 레이어가 있으면 삭제 절차(<see cref="DeleteLayerAsync"/>)를 시작합니다.</summary>
    /// <param name="sender">이벤트를 일으킨 메뉴 항목.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private void SelectedLayerDelete_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedLayer(out var layerName))
        {
            _ = DeleteLayerAsync(layerName);
        }
    }

    /// <summary>확인을 받은 뒤 레이어와 그 위의 모든 객체를 DB 에서 영구 삭제합니다(되돌릴 수 없음).</summary>
    /// <param name="layerName">삭제할 레이어 이름. null 불가.</param>
    private async Task DeleteLayerAsync(string layerName)
    {
        // 1) 삭제 권한과 '지금 레이어를 다시 읽을 수 있는 상태'(예: 미저장 편집 없음)를 확인합니다.
        if (!Require(Permissions.DrawingDelete, "delete layers") || !CanReloadLayers())
        {
            return;
        }

        // 2) 되돌릴 수 없는 작업이므로 기본 선택을 '아니오'로 둔 확인 창을 띄웁니다.
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
            // 3) DB 에서 삭제하고, 삭제 건수를 로그에 기록합니다.
            ShowBusy($"Deleting layer {layerName}...");
            connectionSettings = ReadConnectionSettingsFromUi();
            int removed;
            await using (var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId))
            {
                removed = await repository.DeleteLayerAsync(layerName, changeContext: CurrentChangeContext);
            }

            importLogger.Write($"DELETE layer={layerName} rows={removed}");
            // 4) 화면 상태(레이어 필터, 숨김 목록)에서도 레이어를 제거한 뒤 전체를 다시 불러옵니다.
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

    /// <summary>편집 모드의 현재 상태(대기 중 편집 반영, 삭제된 객체 제외)를 내보내기 인스턴스로 모읍니다.</summary>
    /// <returns>
    /// 내보내기 인스턴스 목록. 편집하지 않은 객체를 앞에 두는데, 같은 블록 이름의 첫 INSERT 가 그 블록 정의의 형상을
    /// 결정하기 때문입니다(편집된 변형이 정의를 오염시키지 않게 함).
    /// </returns>
    private List<CadExportInstance> CollectEditedInstances()
    {
        var items = new List<(bool Changed, CadExportInstance Instance)>();
        // 1) 저장돼 있던 블록 인스턴스: 삭제 표시는 제외하고, 편집이 있으면 편집된 형상/속성을 사용합니다.
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

        // 2) 새로 만든(미저장) 복제/그룹은 대기 중 편집에만 존재하므로 따로 추가합니다.
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

        // 3) 편집 안 된 객체를 먼저 정렬합니다: 같은 블록 이름의 첫 INSERT 가 그 블록 정의의 형상을 정하기 때문입니다.
        return items.OrderBy(item => item.Changed).Select(item => item.Instance).ToList();
    }

    /// <summary>DB 에 저장된 편집 가능 블록 인스턴스를 읽어 내보내기 인스턴스로 변환합니다(숨긴 레이어 제외).</summary>
    /// <returns>내보내기 인스턴스 목록(삽입점 속성이 없는 객체는 제외).</returns>
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

    /// <summary>편집 가능 객체 1개를 내보내기 인스턴스로 변환합니다. 삽입점/회전/스케일은 블록 속성에서 읽습니다.</summary>
    /// <param name="source">원본 객체(블록 이름, 레이어 이름 제공).</param>
    /// <param name="geometry">내보낼 월드 좌표 형상(편집 중이면 편집된 형상).</param>
    /// <param name="attributes">block_insert_x/y, block_rotation_deg, block_scale_x/y 등을 담은 속성 사전.</param>
    /// <returns>변환된 인스턴스. 삽입점 X/Y 가 없으면 null(회전 기본 0, 스케일 기본/0 이면 1, 이름 없으면 "BLOCK").</returns>
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
