using System.Windows;
using System.Windows.Media.Imaging;

// ============================================================================
// AboutWindow : 프로그램 정보(About) 대화상자
// ----------------------------------------------------------------------------
// [호출 위치] MainWindow 의 도움말 > 정보 메뉴 핸들러에서
//   new AboutWindow(version, "Build " + BuildInfo.Number + " (" + BuildInfo.Display + ")") { Owner = this }.ShowDialog()
//   로 모달 표시한다.
// [생성자 매개변수]
//   version   : 프로그램 버전 문자열(예: "1.0.0"). "Version " 접두어가 붙어 VersionText 에 표시.
//   buildLine : 빌드 번호/표시 문자열 한 줄. BuildText 에 그대로 표시.
// [흐름]
//   열기(생성자) → InitializeComponent → 버전/빌드 텍스트 설정
//     → 프로그램 아이콘(app.ico) 디코딩 → 가장 큰 프레임을 IconImage 배경으로 표시
//     → [OK] 버튼(Ok_Click, IsDefault/IsCancel 이므로 Enter/Esc 도 동일) → Close()
//   반환값(DialogResult)은 사용하지 않는다(정보 표시 전용).
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// 프로그램 정보 창. 프로그램 아이콘을 배경 그림으로 깔고 그 위에 버전/빌드 정보를 보여 준다.
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>정보 창을 만들고 버전/빌드 문구와 배경 아이콘을 채운다.</summary>
    /// <param name="version">표시할 버전 문자열("Version " 접두어가 붙는다).</param>
    /// <param name="buildLine">표시할 빌드 정보 한 줄.</param>
    public AboutWindow(string version, string buildLine)
    {
        InitializeComponent();
        // 1) 버전/빌드 문구를 텍스트 블록에 채운다.
        VersionText.Text = "Version " + version;
        BuildText.Text = buildLine;
        // 2) 리소스의 app.ico 를 읽어 배경 이미지로 설정한다(실패해도 창은 떠야 하므로 try/catch).
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

    /// <summary>[OK] 버튼 Click 이벤트 핸들러. 창을 닫는다.</summary>
    /// <param name="sender">클릭된 OK 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
