using System.Windows;
using System.Windows.Media.Imaging;

namespace SpatialEditor.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Window.Icon's default cannot be overridden for Window itself, so every window (the main
        // window and all dialogs) that has no icon of its own gets the application icon when it loads.
        var icon = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/app.ico"));
        icon.Freeze();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window { Icon: null } window)
            {
                window.Icon = icon;
            }
        }));
        base.OnStartup(e);
    }
}
