using System.Windows;
using System.Windows.Media.Imaging;

// ============================================================================
// App : WPF 응용 프로그램 진입점(App.xaml 의 코드 뒤)
// ----------------------------------------------------------------------------
// 흐름도
//   프로세스 시작 → OnStartup
//     → 앱 아이콘(Resources/app.ico)을 BitmapFrame 으로 로드·Freeze
//     → 모든 Window 의 Loaded 이벤트에 클래스 핸들러 등록
//         (아이콘이 없는 창 = 메인 창과 모든 대화상자에 앱 아이콘 부여)
//     → base.OnStartup 으로 StartupUri 창 표시
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// 응용 프로그램 클래스(App.xaml 과 짝). 시작 시 모든 창에 공통 아이콘을 지정하는 전역 처리를 등록합니다.
/// </summary>
public partial class App : Application
{
    /// <summary>응용 프로그램 시작 시 호출됩니다. 아이콘 없는 모든 창에 앱 아이콘을 지정하는 핸들러를 등록합니다.</summary>
    /// <param name="e">시작 인수(명령줄 등).</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        // Window 자체의 Icon 기본값은 재정의할 수 없으므로, 자기 아이콘이 없는 모든 창(메인 창과 대화상자)이 로드될 때 앱 아이콘을 넣어 줍니다.
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
