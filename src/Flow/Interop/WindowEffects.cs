using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static Flow.Interop.NativeMethods;

namespace Flow.Interop;

/// <summary>
/// Makes the whole client area render with a transparent composition background over the DWM frame,
/// so a system backdrop applied by WindHawk (Translucent Windows) shows through behind Flow's content.
/// Flow deliberately does not set its own backdrop type.
/// </summary>
public static class WindowEffects
{
    public static void Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var src = HwndSource.FromHwnd(hwnd);
        if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;

        ExtendFrame(hwnd);

        int darkFlag = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        HideSystemCaptionButtons(hwnd);
    }

    public static void ExtendFrame(IntPtr hwnd)
    {
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    public static void SetDarkMode(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int darkFlag = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));
    }

    /// <summary>
    /// With the frame extended into the whole client area DWM would draw its own min/max/close glyphs
    /// on top of ours. Removing WS_SYSMENU suppresses them; Flow draws its own caption buttons.
    /// </summary>
    public static void HideSystemCaptionButtons(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        if ((style & WS_SYSMENU) != 0)
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~WS_SYSMENU));
    }
}
