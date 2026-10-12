using System.Windows;
using System.Windows.Controls;
using SpatialEditor.Domain;

// ============================================================================
// LayerSelectionWindow : DXF 가져오기 시 가져올 레이어를 체크박스로 고르는 대화상자
// ----------------------------------------------------------------------------
// [호출 위치] MainWindow 에서
//   new LayerSelectionWindow(layers, connectionSettings.SelectedLayers) { Owner = this }
//   로 모달 표시(DXF 파일을 분석한 뒤, 가져올 레이어 선택 단계).
// [생성자 매개변수]
//   layers             : DXF 에서 분석한 레이어 정보 목록(레이어명, 도형 종류, 블록 개수, 엔티티 개수).
//   previouslySelected : 이전에 선택했던 레이어명 집합. null 이거나 비어 있으면 전부 체크한다.
// [흐름]
//   열기 → 레이어마다 CheckBox 생성(내용: 이름 [도형종류] | Blocks | Entities, Tag: 레이어명)
//     → LayerListPanel 에 추가, 이전 선택이 있으면 그 레이어만 체크
//   버튼
//     · Select all  → SelectAll_Click  : 모두 체크
//     · Select none → SelectNone_Click : 모두 해제
//     · OK          → Ok_Click : 체크된 레이어명을 SelectedLayers 에 저장 → DialogResult = true
//     · 취소(XAML 의 취소 버튼/Esc) → 선택 없이 닫힘(호출자가 ShowDialog 결과로 판단)
//   호출자는 ShowDialog() == true 일 때 SelectedLayers 를 읽어 가져오기를 진행한다.
// ============================================================================
namespace SpatialEditor.App;

/// <summary>가져올 DXF 레이어를 체크박스로 선택하는 대화상자.</summary>
public partial class LayerSelectionWindow : Window
{
    /// <summary>생성자에서 만든 레이어별 체크박스 목록(Tag 에 레이어명 보관).</summary>
    private readonly List<CheckBox> layerCheckBoxes = new();

    /// <summary>OK 를 눌렀을 때 체크되어 있던 레이어명 목록(OK 이전에는 빈 배열).</summary>
    public IReadOnlyList<string> SelectedLayers { get; private set; } = Array.Empty<string>();

    /// <summary>레이어 목록으로 체크박스를 구성한다.</summary>
    /// <param name="layers">표시할 레이어 정보 목록.</param>
    /// <param name="previouslySelected">이전 선택 레이어명(대소문자 무시). null/빈 컬렉션이면 전부 선택 상태로 시작.</param>
    public LayerSelectionWindow(IReadOnlyList<LayerInfo> layers, IReadOnlyCollection<string>? previouslySelected)
    {
        InitializeComponent();

        // 1) 이전 선택 목록이 비어 있으면 null(=전체 선택)로 취급한다.
        var preselect = previouslySelected is { Count: > 0 } ? previouslySelected : null;
        // 2) 레이어마다 요약 문구가 담긴 체크박스를 만들어 패널에 추가한다.
        foreach (var layer in layers)
        {
            var checkBox = new CheckBox
            {
                Content = $"{layer.LayerName}  [{layer.GeometryTypes}]  |  Blocks: {layer.InstanceCount:N0}  |  Entities: {layer.EntityCount:N0}",
                Tag = layer.LayerName,
                IsChecked = preselect is null || preselect.Contains(layer.LayerName, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 0, 0, 10)
            };
            layerCheckBoxes.Add(checkBox);
            LayerListPanel.Children.Add(checkBox);
        }
    }

    /// <summary>[Select all] 버튼 Click 핸들러. 모든 레이어를 체크한다.</summary>
    /// <param name="sender">클릭된 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var checkBox in layerCheckBoxes)
        {
            checkBox.IsChecked = true;
        }
    }

    /// <summary>[Select none] 버튼 Click 핸들러. 모든 레이어 체크를 해제한다.</summary>
    /// <param name="sender">클릭된 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var checkBox in layerCheckBoxes)
        {
            checkBox.IsChecked = false;
        }
    }

    /// <summary>[OK] 버튼 Click 핸들러. 체크된 레이어명을 SelectedLayers 에 담고 대화상자를 성공(true)으로 닫는다.</summary>
    /// <param name="sender">클릭된 OK 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedLayers = layerCheckBoxes
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => (string)checkBox.Tag)
            .ToArray();
        DialogResult = true;
    }
}
