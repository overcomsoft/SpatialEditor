using System.Windows;
using System.Windows.Media.Imaging;

namespace SpatialEditor.App;

/// <summary>About box with the program icon as its background artwork.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow(string version, string buildLine)
    {
        InitializeComponent();
        VersionText.Text = "Version " + version;
        BuildText.Text = buildLine;
        try
        {
            // The .ico holds several sizes; show the largest one.
            var decoder = new IconBitmapDecoder(
                new Uri("pack://application:,,,/Resources/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            IconImage.Source = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
        }
        catch (Exception)
        {
            // Without the icon the red background alone is shown.
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
