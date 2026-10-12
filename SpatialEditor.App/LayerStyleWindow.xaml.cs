using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

// ============================================================================
// LayerStyleWindow : 레이어 속성(이름, 선 색/두께, 면 채움 색/투명도) 편집 대화상자
// ----------------------------------------------------------------------------
// [호출 위치] MainWindow 의 레이어 속성 핸들러에서
//   new LayerStyleWindow(layerName, GetLayerColor(..), GetLayerThickness(..), fillColor, fillOpacity)
//   를 모달로 표시한다.
// [생성자 매개변수]
//   layerName   : 현재 레이어 이름(이름 변경 감지를 위해 originalName 으로 보관).
//   color       : 현재 선 색.
//   thickness   : 현재 선 두께(px). 슬라이더 범위로 보정된다.
//   fillColor   : 면 채움 색. null 이면 "선 색과 동일" 체크 상태로 시작.
//   fillOpacity : 면 채움 불투명도 0..1 (5% 단위로 반올림해 슬라이더에 반영).
// [흐름]
//   열기 → 값들을 컨트롤에 채움(이름, 선색/채움색 ColorEditor, 투명도/두께 슬라이더)
//     → ColorChanged 구독 → FillSame_Changed 호출로 채움 편집기 활성/비활성 + 미리보기 첫 그리기
//   사용자 동작
//     · 색 변경(ColorEditor.ColorChanged)  → UpdatePreview
//     · 두께 슬라이더 → Thickness_Changed  → 텍스트 갱신 + UpdatePreview
//     · 투명도 슬라이더 → Opacity_Changed  → 텍스트 갱신 + UpdatePreview
//     · "Same color as line" 체크/해제 → FillSame_Changed → 채움 편집기 활성/비활성
//     · Reset style → Reset_Click : ResetRequested = true, DialogResult = true (기본 스타일로 되돌림 요청)
//     · OK → Ok_Click : 이름이 비어 있으면 경고 후 중단, 아니면 두께 확정 후 DialogResult = true
//   호출자는 ShowDialog() == true 이면 ResetRequested 를 먼저 보고, 아니면
//   LayerName/NameChanged/SelectedColor/SelectedThickness/SelectedFillColor/SelectedFillOpacity 를 읽어 적용한다.
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// 레이어 속성 창: 이름, 선 색/두께, 면(폴리곤) 채움 색/불투명도를 편집한다.
/// 지도가 도형을 그리는 방식(둥근 이음/끝 모양)과 동일하게 그린 실시간 미리보기를 제공한다.
/// </summary>
public partial class LayerStyleWindow : Window
{
    /// <summary>창을 열 때의 레이어 이름(이름이 바뀌었는지 비교하는 기준).</summary>
    private readonly string originalName;

    /// <summary>입력된 레이어 이름(앞뒤 공백 제거).</summary>
    public string LayerName => NameTextBox.Text.Trim();
    /// <summary>레이어 이름이 원래 이름과 다르면 true(대소문자까지 구분).</summary>
    public bool NameChanged => !string.Equals(LayerName, originalName, StringComparison.Ordinal);
    /// <summary>선택된 선 색.</summary>
    public Color SelectedColor => LineColorEditor.Color;
    /// <summary>선택된 선 두께(px).</summary>
    public double SelectedThickness { get; private set; }
    /// <summary>면 채움 색. "선 색과 동일"이 체크되어 있으면 null(선 색을 따름).</summary>
    public Color? SelectedFillColor => FillSameCheckBox.IsChecked == true ? null : FillColorEditor.Color;
    /// <summary>면 채움 불투명도(0..1). 슬라이더 값(0~100)을 100으로 나눈 값.</summary>
    public double SelectedFillOpacity => OpacitySlider.Value / 100.0;
    /// <summary>[Reset style] 을 눌러 기본 스타일로 되돌리기를 요청했으면 true.</summary>
    public bool ResetRequested { get; private set; }

    /// <summary>현재 스타일 값으로 창을 초기화한다.</summary>
    /// <param name="layerName">레이어 이름.</param>
    /// <param name="color">현재 선 색.</param>
    /// <param name="thickness">현재 선 두께(px, 슬라이더 범위로 보정).</param>
    /// <param name="fillColor">현재 면 채움 색(null 이면 선 색과 동일).</param>
    /// <param name="fillOpacity">현재 면 채움 불투명도(0..1).</param>
    public LayerStyleWindow(string layerName, Color color, double thickness, Color? fillColor, double fillOpacity)
    {
        InitializeComponent();
        // 1) 전달받은 값을 컨트롤에 채운다(투명도는 5% 단위로 반올림, 두께는 슬라이더 범위로 제한).
        originalName = layerName;
        NameTextBox.Text = layerName;
        LineColorEditor.SetColor(color);
        FillColorEditor.SetColor(fillColor ?? color);
        FillSameCheckBox.IsChecked = fillColor is null;
        OpacitySlider.Value = Math.Clamp(Math.Round(fillOpacity * 100 / 5) * 5, 0, 100);
        ThicknessSlider.Value = Math.Clamp(thickness, ThicknessSlider.Minimum, ThicknessSlider.Maximum);
        SelectedThickness = ThicknessSlider.Value;
        // 2) 색이 바뀔 때마다 미리보기를 다시 그리도록 구독하고, 채움 편집기 활성 상태/첫 미리보기를 갱신한다.
        LineColorEditor.ColorChanged += (_, _) => UpdatePreview();
        FillColorEditor.ColorChanged += (_, _) => UpdatePreview();
        FillSame_Changed(this, new RoutedEventArgs());
    }

    /// <summary>색을 "#RRGGBB" 문자열로 바꾼다(ColorEditor.ToHex 위임).</summary>
    /// <param name="color">변환할 색.</param>
    /// <returns>16진수 색 문자열.</returns>
    internal static string ToHex(Color color) => ColorEditor.ToHex(color);

    /// <summary>"Same color as line" 체크박스의 Checked/Unchecked 이벤트 핸들러(생성자에서도 직접 호출). 체크 시 채움 색 편집기를 비활성(반투명)으로 만든다.</summary>
    /// <param name="sender">FillSameCheckBox(또는 생성자 호출 시 this).</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void FillSame_Changed(object sender, RoutedEventArgs e)
    {
        if (FillColorEditor is null)
        {
            return;
        }

        FillColorEditor.IsEnabled = FillSameCheckBox.IsChecked != true;
        FillColorEditor.Opacity = FillColorEditor.IsEnabled ? 1 : 0.4;
        UpdatePreview();
    }

    /// <summary>ThicknessSlider 의 ValueChanged 핸들러. 두께 값과 "N px" 표시를 갱신하고 미리보기를 다시 그린다.</summary>
    /// <param name="sender">두께 슬라이더.</param>
    /// <param name="e">이전/새 값 인자.</param>
    private void Thickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThicknessText is null)
        {
            return;
        }

        SelectedThickness = ThicknessSlider.Value;
        ThicknessText.Text = $"{SelectedThickness:0.##} px";
        UpdatePreview();
    }

    /// <summary>OpacitySlider 의 ValueChanged 핸들러. "N%" 표시를 갱신하고 미리보기를 다시 그린다.</summary>
    /// <param name="sender">불투명도 슬라이더.</param>
    /// <param name="e">이전/새 값 인자.</param>
    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityText is null)
        {
            return;
        }

        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        UpdatePreview();
    }

    /// <summary>미리보기 캔버스(PreviewCanvas)에 지그재그 선과 사각형을 현재 설정대로 다시 그린다.</summary>
    private void UpdatePreview()
    {
        // XAML 초기화 중에는 컨트롤이 아직 만들어지지 않았을 수 있으므로 건너뛴다.
        if (PreviewCanvas is null || ThicknessSlider is null || OpacitySlider is null
            || FillSameCheckBox is null || FillColorEditor is null || LineColorEditor is null)
        {
            return;
        }

        // 1) 이전 미리보기를 지우고 선 색/채움 색(채움이 null 이면 선 색)을 정한다.
        PreviewCanvas.Children.Clear();
        var stroke = new SolidColorBrush(SelectedColor);
        var fill = SelectedFillColor ?? SelectedColor;
        // 2) 선 두께/이음/끝 모양을 지도와 같게(Round) 설정한 지그재그 선을 만든다.
        var zigzag = new Polyline
        {
            Points = new PointCollection
            {
                new Point(20, 50), new Point(70, 18), new Point(120, 50), new Point(170, 18), new Point(220, 50)
            },
            Stroke = stroke,
            StrokeThickness = ThicknessSlider.Value,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        // 3) 채움 색에 불투명도(알파)를 적용한 사각형(폴리곤 미리보기)을 만든다.
        var box = new Rectangle
        {
            Width = 60,
            Height = 40,
            Stroke = stroke,
            Fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(SelectedFillOpacity * 255), fill.R, fill.G, fill.B)),
            StrokeThickness = ThicknessSlider.Value,
            StrokeLineJoin = PenLineJoin.Round
        };
        // 4) 사각형을 오른쪽에 배치하고 캔버스에 추가한다.
        Canvas.SetLeft(box, 270);
        Canvas.SetTop(box, 14);
        PreviewCanvas.Children.Add(zigzag);
        PreviewCanvas.Children.Add(box);
    }

    /// <summary>[Reset style] 버튼 Click 핸들러. 기본 스타일 복원을 요청하고 대화상자를 true 로 닫는다.</summary>
    /// <param name="sender">클릭된 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        ResetRequested = true;
        DialogResult = true;
    }

    /// <summary>[OK] 버튼 Click 핸들러. 이름이 비었으면 경고 후 열어 두고, 아니면 두께를 확정하고 true 로 닫는다.</summary>
    /// <param name="sender">클릭된 OK 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 레이어 이름은 필수이다.
        if (LayerName.Length == 0)
        {
            MessageBox.Show(this, "Enter a layer name.", "Layer properties", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedThickness = ThicknessSlider.Value;
        DialogResult = true;
    }
}
