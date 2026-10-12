using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// ============================================================================
// ColorEditor : 색상 선택 UserControl (창이 아니라 다른 창 안에 배치되는 컨트롤)
// ----------------------------------------------------------------------------
// [사용 위치] LayerStyleWindow.xaml 의 LineColorEditor(선 색) / FillColorEditor(채움 색).
// [구성] 팔레트(PalettePanel, 12색 견본) + R/G/B 슬라이더(RedSlider/GreenSlider/BlueSlider)
//        + 값 표시(RedText/GreenText/BlueText) + 16진수 입력(HexTextBox, 예: #1F77B4).
// [흐름]
//   생성(생성자) → 팔레트 견본 Border 를 PalettePanel 에 추가(클릭 시 SetColor)
//     → 기본색 #1F77B4 로 SetColor 호출
//   사용자 동작
//     · 견본 클릭            → SetColor(견본색)
//     · 슬라이더 이동        → Channel_Changed → SetColor(R,G,B 조합)
//     · 16진수 직접 입력     → Hex_TextChanged → 형식 "#RRGGBB" 검증 통과 시 SetColor
//   SetColor → 슬라이더/16진수/숫자 표시 동기화(updating 플래그로 재진입 방지)
//     → Color 프로퍼티 갱신 → ColorChanged 이벤트 발생(호출 창이 미리보기 갱신)
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// 팔레트, RGB 슬라이더, 16진수 입력 상자로 이루어진 색상 선택 컨트롤.
/// 색이 바뀔 때마다 <see cref="ColorChanged"/> 이벤트를 발생시킨다.
/// </summary>
public partial class ColorEditor : UserControl
{
    /// <summary>팔레트에 표시할 12개 기본 색상의 16진수(#RRGGBB) 목록.</summary>
    private static readonly string[] PaletteHex =
    {
        "#1F77B4", "#FF7F0E", "#2CA02C", "#D62728", "#9467BD", "#8C564B",
        "#E377C2", "#7F7F7F", "#BCBD22", "#17BECF", "#000000", "#FFFFFF"
    };

    /// <summary>SetColor 가 컨트롤 값을 갱신하는 동안 true. 슬라이더/텍스트 변경 이벤트의 재진입(무한 루프)을 막는다.</summary>
    private bool updating;

    /// <summary>현재 선택된 색(알파 없는 불투명 RGB). SetColor 로만 바뀐다.</summary>
    public Color Color { get; private set; }
    /// <summary>선택 색이 바뀔 때마다 발생한다(sender 는 이 컨트롤, 인자는 비어 있음).</summary>
    public event EventHandler? ColorChanged;

    /// <summary>컨트롤을 초기화하고 팔레트 견본을 만든 뒤 기본색(#1F77B4)을 선택한다.</summary>
    public ColorEditor()
    {
        InitializeComponent();
        // 1) 팔레트 색마다 22x22 견본 Border 를 만들어 클릭하면 해당 색이 선택되도록 한다.
        foreach (var hex in PaletteHex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(color), BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = hex
            };
            swatch.MouseLeftButtonDown += (_, _) => SetColor(color);
            PalettePanel.Children.Add(swatch);
        }

        // 2) 초기 선택색을 설정(슬라이더/16진수/ColorChanged 까지 한 번에 동기화).
        SetColor(Color.FromRgb(0x1F, 0x77, 0xB4));
    }

    /// <summary>
    /// 색을 지정하고 모든 입력 요소(슬라이더, 16진수, 숫자 표시)를 동기화한 뒤 <see cref="ColorChanged"/> 를 발생시킨다.
    /// </summary>
    /// <param name="color">설정할 색(알파는 무시되고 R/G/B 만 사용).</param>
    public void SetColor(Color color)
    {
        // 1) 슬라이더/텍스트를 코드로 바꾸는 동안 변경 이벤트가 다시 SetColor 를 부르지 않도록 막는다.
        updating = true;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        HexTextBox.Text = ToHex(color);
        updating = false;
        // 2) 알파를 제거한 불투명 색을 저장하고 숫자 표시를 갱신한 뒤 구독자에게 알린다.
        Color = Color.FromRgb(color.R, color.G, color.B);
        RedText.Text = color.R.ToString(CultureInfo.InvariantCulture);
        GreenText.Text = color.G.ToString(CultureInfo.InvariantCulture);
        BlueText.Text = color.B.ToString(CultureInfo.InvariantCulture);
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>색을 "#RRGGBB" 대문자 16진수 문자열로 변환한다.</summary>
    /// <param name="color">변환할 색.</param>
    /// <returns>예: "#1F77B4".</returns>
    internal static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>R/G/B 슬라이더의 ValueChanged 이벤트 핸들러.</summary>
    /// <param name="sender">값이 바뀐 슬라이더(RedSlider/GreenSlider/BlueSlider).</param>
    /// <param name="e">이전/새 슬라이더 값을 담은 인자.</param>
    private void Channel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 코드가 값을 갱신 중이거나 XAML 로딩 중(컨트롤이 아직 null)이면 무시한다.
        if (updating || HexTextBox is null || BlueText is null)
        {
            return;
        }

        // 세 슬라이더 값을 조합해 색을 확정한다.
        SetColor(Color.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value));
    }

    /// <summary>HexTextBox 의 TextChanged 이벤트 핸들러. "#RRGGBB" 형식이면 색을 반영한다.</summary>
    /// <param name="sender">16진수 입력 TextBox.</param>
    /// <param name="e">텍스트 변경 인자.</param>
    private void Hex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updating)
        {
            return;
        }

        // 정확히 '#'+6자리 16진수일 때만 반영한다(입력 도중의 불완전한 값은 무시).
        var text = HexTextBox.Text.Trim();
        if (text.Length == 7 && text[0] == '#'
            && int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            SetColor(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
    }
}
