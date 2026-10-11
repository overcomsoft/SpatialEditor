using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SpatialEditor.App;

/// <summary>Color picker made of a palette, RGB sliders and a hex box; raises <see cref="ColorChanged"/> on every change.</summary>
public partial class ColorEditor : UserControl
{
    private static readonly string[] PaletteHex =
    {
        "#1F77B4", "#FF7F0E", "#2CA02C", "#D62728", "#9467BD", "#8C564B",
        "#E377C2", "#7F7F7F", "#BCBD22", "#17BECF", "#000000", "#FFFFFF"
    };

    private bool updating;

    public Color Color { get; private set; }
    public event EventHandler? ColorChanged;

    public ColorEditor()
    {
        InitializeComponent();
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

        SetColor(Color.FromRgb(0x1F, 0x77, 0xB4));
    }

    public void SetColor(Color color)
    {
        updating = true;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        HexTextBox.Text = ToHex(color);
        updating = false;
        Color = Color.FromRgb(color.R, color.G, color.B);
        RedText.Text = color.R.ToString(CultureInfo.InvariantCulture);
        GreenText.Text = color.G.ToString(CultureInfo.InvariantCulture);
        BlueText.Text = color.B.ToString(CultureInfo.InvariantCulture);
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

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
}
