using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

// ============================================================================
// MainWindow.Capture.cs : partial class MainWindow 중 "화면 캡처(프로그램 영역 → 클립보드)" 담당 부분
// ----------------------------------------------------------------------------
// 흐름도 (CopyScreen_Click)
//   보이는 프로그램 창 수집(VisibleProgramWindows: 메인 창 + 대화상자 + 메시지 상자 + 열린 메뉴)
//     → 창마다 GetWindowRect(그림자 포함 전체)와 DwmGetWindowAttribute(보이는 프레임) 조회
//     → 모든 프레임의 합집합 사각형 계산 → 같은 크기의 32bpp 비트맵(canvas) 생성
//     → [1단계] CopyFromScreen: 화면에 보이는 그대로 복사(대화상자 포함)
//         → IsMostlyBlack(30% 이상 검정/투명)이면 실패로 간주
//     → [2단계] PrintWindow: 창마다 스스로 그리게 해 아래→위 순서로 합성(보이는 프레임만 클립)
//         → 여전히 IsMostlyBlack 이면(원격/가상 디스플레이 등)
//     → [3단계] CopyScreenWpf: WPF 콘텐츠를 직접 렌더링해 복사 후 종료
//     → 성공 시 HBITMAP → BitmapSource(Freeze) → Clipboard.SetImage → 상태줄 안내(DeleteObject 로 GDI 핸들 해제)
//   예외는 메시지 상자로 안내
// ============================================================================
namespace SpatialEditor.App;

/// <summary>MainWindow 의 화면 캡처 부분: 프로그램 영역(메인 창 + 열린 모든 대화상자, 제목 표시줄 포함)을 클립보드로 복사합니다.</summary>
public partial class MainWindow
{
    /// <summary>Win32 RECT 와 같은 메모리 배치의 사각형(화면 픽셀 좌표). P/Invoke 인수/반환용.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        // 필드: Left/Top = 왼쪽 위 모서리, Right/Bottom = 오른쪽 아래 모서리(포함하지 않는 끝, 화면 좌표 px).
        public int Left, Top, Right, Bottom;
    }

    /// <summary>user32 GetWindowRect: 창의 전체 사각형(보이지 않는 크기 조절 테두리/그림자 포함)을 화면 좌표로 얻습니다.</summary>
    /// <param name="hwnd">대상 창 핸들.</param>
    /// <param name="rect">결과 사각형(출력).</param>
    /// <returns>성공하면 true.</returns>
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    /// <summary>user32 PrintWindow: 창이 스스로 내용을 지정한 DC 에 그리도록 요청합니다(가려진 창도 캡처 가능).</summary>
    /// <param name="hwnd">대상 창 핸들.</param>
    /// <param name="hdc">그릴 대상 장치 컨텍스트.</param>
    /// <param name="flags">옵션 플래그(여기서는 PrintFullContent=2, 전체 콘텐츠).</param>
    /// <returns>성공하면 true.</returns>
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    /// <summary>dwmapi DwmGetWindowAttribute: DWM 이 관리하는 창 속성을 얻습니다(여기서는 보이는 프레임 경계).</summary>
    /// <param name="hwnd">대상 창 핸들.</param>
    /// <param name="attribute">속성 번호(DwmExtendedFrameBounds=9).</param>
    /// <param name="rect">결과 사각형(출력, 그림자/보이지 않는 테두리 제외).</param>
    /// <param name="size">rect 구조체의 바이트 크기.</param>
    /// <returns>0(S_OK)이면 성공, 그 외는 실패 HRESULT.</returns>
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect rect, int size);

    /// <summary>gdi32 DeleteObject: GDI 개체(HBITMAP 등)를 해제합니다. GetHbitmap 이 만든 핸들은 반드시 해제해야 누수가 없습니다.</summary>
    /// <param name="handle">해제할 GDI 핸들.</param>
    /// <returns>성공하면 true.</returns>
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    /// <summary>DwmGetWindowAttribute 의 DWMWA_EXTENDED_FRAME_BOUNDS 속성 번호(9): 그림자를 뺀 실제 보이는 창 경계.</summary>
    private const int DwmExtendedFrameBounds = 9;
    /// <summary>PrintWindow 의 PW_RENDERFULLCONTENT 플래그(2): DWM 합성 콘텐츠까지 전부 그리도록 요청.</summary>
    private const uint PrintFullContent = 2;

    /// <summary>EnumThreadWindows 콜백 형식.</summary>
    /// <param name="hwnd">열거된 창 핸들.</param>
    /// <param name="lParam">호출 시 전달한 사용자 값.</param>
    /// <returns>계속 열거하려면 true.</returns>
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>user32 EnumThreadWindows: 지정 스레드가 소유한 최상위 창을 위(Z 순서 앞)에서 아래로 열거합니다.</summary>
    /// <param name="threadId">대상 스레드 ID.</param>
    /// <param name="callback">창마다 호출될 콜백.</param>
    /// <param name="lParam">콜백에 전달할 사용자 값.</param>
    /// <returns>열거가 수행되면 true.</returns>
    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

    /// <summary>user32 IsWindowVisible: 창이 화면에 보이는 상태인지 확인합니다.</summary>
    /// <param name="hwnd">대상 창 핸들.</param>
    /// <returns>보이면 true.</returns>
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>kernel32 GetCurrentThreadId: 호출한 스레드(UI 스레드)의 ID 를 얻습니다.</summary>
    /// <returns>현재 스레드 ID.</returns>
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>이 프로그램의 보이는 최상위 창 전부(메인 창, 대화상자, 메시지 상자, 열린 메뉴)를 아래에서 위 순서로 모읍니다.</summary>
    /// <returns>창 핸들 목록. 첫 항목은 항상 메인 창(합성 시 가장 먼저 그려져 맨 아래에 놓임).</returns>
    private List<IntPtr> VisibleProgramWindows()
    {
        // 1) UI 스레드 소유 창을 열거해 보이는 것만 담습니다(열거 순서는 위→아래).
        var found = new List<IntPtr>();
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                found.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        // 2) 합성 순서를 위해 뒤집고, 메인 창을 맨 앞(맨 아래층)으로 옮깁니다.
        found.Reverse(); // enumeration is top to bottom
        var main = new WindowInteropHelper(this).Handle;
        found.Remove(main);
        found.Insert(0, main);
        return found;
    }

    /// <summary>성긴 격자(가로·세로 약 40칸)로 픽셀을 표본 추출해, 30% 이상이 검정/투명이면 캡처 실패로 판정합니다.</summary>
    /// <param name="bitmap">검사할 비트맵.</param>
    /// <returns>검정/투명 표본 비율이 30% 이상이면 true(실패한 캡처).</returns>
    private static bool IsMostlyBlack(Drawing.Bitmap bitmap)
    {
        var lit = 0;
        var total = 0;
        for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 40))
        {
            for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 40))
            {
                var c = bitmap.GetPixel(x, y);
                total++;
                if (c.A > 0 && (c.R > 12 || c.G > 12 || c.B > 12))
                {
                    lit++;
                }
            }
        }

        return (total - lit) * 10 > total * 3;
    }

    /// <summary>
    /// "화면 복사" 명령: 프로그램 영역을 이미지로 만들어 클립보드에 넣습니다.
    /// 화면 복사 → 창별 PrintWindow → WPF 직접 렌더링의 3단계로 대체하며 시도합니다.
    /// </summary>
    /// <param name="sender">이벤트를 일으킨 메뉴/버튼.</param>
    /// <param name="e">라우트 이벤트 인수(사용 안 함).</param>
    private void CopyScreen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 0) 보이는 창마다 전체 사각형(Full)과 실제 보이는 프레임(Frame)을 구합니다. DWM 조회 실패 시 Full 로 대체.
            var items = new List<(IntPtr Handle, NativeRect Full, NativeRect Frame)>();
            foreach (var handle in VisibleProgramWindows())
            {
                if (handle == IntPtr.Zero || !GetWindowRect(handle, out var full) || full.Right <= full.Left || full.Bottom <= full.Top)
                {
                    continue;
                }

                if (DwmGetWindowAttribute(handle, DwmExtendedFrameBounds, out var frame, Marshal.SizeOf<NativeRect>()) != 0)
                {
                    frame = full;
                }

                items.Add((handle, full, frame));
            }

            if (items.Count == 0)
            {
                CopyScreenWpf();
                return;
            }

            // 합집합 영역(모든 프레임을 감싸는 사각형)을 캡처 범위로 삼습니다.
            var left = items.Min(i => i.Frame.Left);
            var top = items.Min(i => i.Frame.Top);
            var right = items.Max(i => i.Frame.Right);
            var bottom = items.Max(i => i.Frame.Bottom);
            var width = right - left;
            var height = bottom - top;

            // 합성 결과를 담을 비트맵을 만듭니다.
            using var canvas = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppArgb);

            // 1) 1단계: 화면에 보이는 것을 그대로 복사합니다. 사용자가 보는 모습 그대로이며 대화상자도 포함됩니다.
            using (var sg = Drawing.Graphics.FromImage(canvas))
            {
                sg.CopyFromScreen(left, top, 0, 0, new Drawing.Size(width, height));
            }

            if (IsMostlyBlack(canvas))
            {
                // 2) 2단계: 각 창에 스스로 그리도록 요청합니다(PrintWindow). 흰색 바탕에 아래층부터 차례로 붙입니다.
                using var g = Drawing.Graphics.FromImage(canvas);
                g.Clear(Drawing.Color.White);
                foreach (var item in items)
                {
                    var w = item.Full.Right - item.Full.Left;
                    var h = item.Full.Bottom - item.Full.Top;
                    using var shot = new Drawing.Bitmap(w, h, Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var sg = Drawing.Graphics.FromImage(shot))
                    {
                        var hdc = sg.GetHdc();
                        try
                        {
                            PrintWindow(item.Handle, hdc, PrintFullContent);
                        }
                        finally
                        {
                            sg.ReleaseHdc(hdc);
                        }
                    }

                    // 보이는 프레임 영역만 붙여, 보이지 않는 크기 조절 테두리가 아래 창을 덮지 않게 합니다.
                    var frameRect = new Drawing.Rectangle(item.Frame.Left - left, item.Frame.Top - top,
                        item.Frame.Right - item.Frame.Left, item.Frame.Bottom - item.Frame.Top);
                    g.SetClip(frameRect);
                    g.DrawImage(shot, item.Full.Left - left, item.Full.Top - top, w, h);
                    g.ResetClip();
                }
            }

            // 3) 3단계: 두 방법 모두 (일부) 검은 그림이면(예: 일부 원격/가상 디스플레이) WPF 콘텐츠를 직접 렌더링해 복사합니다.
            if (IsMostlyBlack(canvas))
            {
                CopyScreenWpf();
                return;
            }

            // 4) 성공: HBITMAP 을 BitmapSource 로 바꿔 Freeze 후 클립보드에 넣고, GDI 핸들은 반드시 해제합니다.
            var hbitmap = canvas.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                Clipboard.SetImage(source);
            }
            finally
            {
                DeleteObject(hbitmap);
            }

            StatusText.Text = "Program area copied to the clipboard (" + width + " x " + height + " px, " + items.Count + " window(s)).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Cannot copy the screen: " + ex.Message, "Copy screen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
