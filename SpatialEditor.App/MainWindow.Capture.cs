using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace SpatialEditor.App;

/// <summary>Copies the program area (main window plus every open dialog, title bars included) to the clipboard.</summary>
public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect rect, int size);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    private const int DwmExtendedFrameBounds = 9;
    private const uint PrintFullContent = 2;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>Every visible top-level window of this program (main window, dialogs, message boxes, open menus), bottom to top.</summary>
    private List<IntPtr> VisibleProgramWindows()
    {
        var found = new List<IntPtr>();
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                found.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        found.Reverse(); // enumeration is top to bottom
        var main = new WindowInteropHelper(this).Handle;
        found.Remove(main);
        found.Insert(0, main);
        return found;
    }

    /// <summary>True when a coarse pixel sample shows that 30% or more of the image is black or transparent (a failed capture).</summary>
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

    private void CopyScreen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
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

            var left = items.Min(i => i.Frame.Left);
            var top = items.Min(i => i.Frame.Top);
            var right = items.Max(i => i.Frame.Right);
            var bottom = items.Max(i => i.Frame.Bottom);
            var width = right - left;
            var height = bottom - top;

            using var canvas = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppArgb);

            // 1) Copy what is on the screen: it is exactly what the user sees, dialogs included.
            using (var sg = Drawing.Graphics.FromImage(canvas))
            {
                sg.CopyFromScreen(left, top, 0, 0, new Drawing.Size(width, height));
            }

            if (IsMostlyBlack(canvas))
            {
                // 2) Ask every window to draw itself.
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

                    // Only the visible frame is pasted, so the invisible resize border never covers the window below.
                    var frameRect = new Drawing.Rectangle(item.Frame.Left - left, item.Frame.Top - top,
                        item.Frame.Right - item.Frame.Left, item.Frame.Bottom - item.Frame.Top);
                    g.SetClip(frameRect);
                    g.DrawImage(shot, item.Full.Left - left, item.Full.Top - top, w, h);
                    g.ResetClip();
                }
            }

            // 3) Both ways gave a (partly) black picture, e.g. on some remote or virtual displays: render the WPF content ourselves.
            if (IsMostlyBlack(canvas))
            {
                CopyScreenWpf();
                return;
            }

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
