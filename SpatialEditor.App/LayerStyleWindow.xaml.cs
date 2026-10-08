using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SpatialEditor.App;

/// <summary>
/// Lets the user pick a layer's line color (palette, RGB sliders or hex) and thickness, with a
/// live preview drawn the same way the map draws lines (round joins/caps).
/// </summary>
public partial class LayerStyleWindow : Window
{
    private static readonly string[] PaletteHex =
    {
        "#1F77B4", "#FF7F0E", "#2CA02C", "#D62728", "#9467BD", "#8C564B",
        "#E377C2", "#7F7F7F", "#BCBD22", "#17BECF", "#000000", "#FFFFFF"
    };

    private bool updating;

    public Color SelectedColor { get; private set; }
    public double SelectedThickness { get; private set; }
    public bool ResetRequested { get; private set; }

    public LayerStyleWindow(string layerName, Color color, double thickness)
    {
        InitializeComponent();
        LayerNameText.Text = $"Layer: {layerName}";
        BuildPalette();
        SetColor(color);
        ThicknessSlider.Value = Math.Clamp(thickness, ThicknessSlider.Minimum, ThicknessSlider.Maximum);
        UpdatePreview();
    }

    private void BuildPalette()
    {
        foreach (var hex in PaletteHex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = hex
            };
            swatch.MouseLeftButtonDown += (_, _) => SetColor(color);
            PalettePanel.Children.Add(swatch);
        }
    }

    private void SetColor(Color color)
    {
        updating = true;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        HexTextBox.Text = ToHex(color);
        updating = false;
        SelectedColor = Color.FromRgb(color.R, color.G, color.B);
        UpdateChannelTexts();
        UpdatePreview();
    }

    internal static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void UpdateChannelTexts()
    {
        RedText.Text = ((int)RedSlider.Value).ToString(CultureInfo.InvariantCulture);
        GreenText.Text = ((int)GreenSlider.Value).ToString(CultureInfo.InvariantCulture);
        BlueText.Text = ((int)BlueSlider.Value).ToString(CultureInfo.InvariantCulture);
    }

    private void Channel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating || HexTextBox is null || BlueText is null)
        {
            return;
        }

        SetColor(Color.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value));
    }

    private void Hex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updating)
        {
            return;
        }

        var text = HexTextBox.Text.Trim();
        if (text.Length == 7 && text[0] == '#'
            && int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            SetColor(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
    }

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

    private void UpdatePreview()
    {
        if (PreviewCanvas is null || ThicknessSlider is null)
        {
            return;
        }

        PreviewCanvas.Children.Clear();
        var brush = new SolidColorBrush(SelectedColor);
        // Same stroke settings as the map: round joins and caps keep touching segments connected.
        var zigzag = new Polyline
        {
            Points = new PointCollection
            {
                new Point(20, 50), new Point(70, 18), new Point(120, 50), new Point(170, 18), new Point(220, 50)
            },
            Stroke = brush,
            StrokeThickness = ThicknessSlider.Value,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        var box = new Rectangle
        {
            Width = 60,
            Height = 40,
            Stroke = brush,
            StrokeThickness = ThicknessSlider.Value,
            StrokeLineJoin = PenLineJoin.Round
        };
        Canvas.SetLeft(box, 270);
        Canvas.SetTop(box, 14);
        PreviewCanvas.Children.Add(zigzag);
        PreviewCanvas.Children.Add(box);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        ResetRequested = true;
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedThickness = ThicknessSlider.Value;
        DialogResult = true;
    }
}
