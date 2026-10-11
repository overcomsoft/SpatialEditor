using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SpatialEditor.App;

/// <summary>
/// Layer properties: name, line color/thickness and polygon fill color/opacity, with a live
/// preview drawn the same way the map draws shapes (round joins/caps).
/// </summary>
public partial class LayerStyleWindow : Window
{
    private readonly string originalName;

    public string LayerName => NameTextBox.Text.Trim();
    public bool NameChanged => !string.Equals(LayerName, originalName, StringComparison.Ordinal);
    public Color SelectedColor => LineColorEditor.Color;
    public double SelectedThickness { get; private set; }
    /// <summary>Fill color, or null when the fill follows the line color.</summary>
    public Color? SelectedFillColor => FillSameCheckBox.IsChecked == true ? null : FillColorEditor.Color;
    /// <summary>Fill opacity 0..1.</summary>
    public double SelectedFillOpacity => OpacitySlider.Value / 100.0;
    public bool ResetRequested { get; private set; }

    public LayerStyleWindow(string layerName, Color color, double thickness, Color? fillColor, double fillOpacity)
    {
        InitializeComponent();
        originalName = layerName;
        NameTextBox.Text = layerName;
        LineColorEditor.SetColor(color);
        FillColorEditor.SetColor(fillColor ?? color);
        FillSameCheckBox.IsChecked = fillColor is null;
        OpacitySlider.Value = Math.Clamp(Math.Round(fillOpacity * 100 / 5) * 5, 0, 100);
        ThicknessSlider.Value = Math.Clamp(thickness, ThicknessSlider.Minimum, ThicknessSlider.Maximum);
        SelectedThickness = ThicknessSlider.Value;
        LineColorEditor.ColorChanged += (_, _) => UpdatePreview();
        FillColorEditor.ColorChanged += (_, _) => UpdatePreview();
        FillSame_Changed(this, new RoutedEventArgs());
    }

    internal static string ToHex(Color color) => ColorEditor.ToHex(color);

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

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityText is null)
        {
            return;
        }

        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (PreviewCanvas is null || ThicknessSlider is null || OpacitySlider is null
            || FillSameCheckBox is null || FillColorEditor is null || LineColorEditor is null)
        {
            return;
        }

        PreviewCanvas.Children.Clear();
        var stroke = new SolidColorBrush(SelectedColor);
        var fill = SelectedFillColor ?? SelectedColor;
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
        var box = new Rectangle
        {
            Width = 60,
            Height = 40,
            Stroke = stroke,
            Fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(SelectedFillOpacity * 255), fill.R, fill.G, fill.B)),
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
        if (LayerName.Length == 0)
        {
            MessageBox.Show(this, "Enter a layer name.", "Layer properties", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedThickness = ThicknessSlider.Value;
        DialogResult = true;
    }
}
