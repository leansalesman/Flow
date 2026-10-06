using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.UI.ViewManagement;
using WinColor = Windows.UI.Color;

namespace Flow.Services;

/// <summary>
/// Follows the Windows accent color and light/dark app theme, live.
/// Publishes brushes into Application resources (use DynamicResource).
/// </summary>
public sealed class ThemeService
{
    private readonly UISettings _ui = new();
    private readonly Dispatcher _dispatcher;

    public event Action? ThemeChanged;
    public bool IsDark { get; private set; } = true;
    public Color Accent { get; private set; } = Color.FromRgb(0x4C, 0xC2, 0xFF);
    public Color AccentLight { get; private set; } = Color.FromRgb(0x99, 0xEB, 0xFF);
    public Color AccentDark { get; private set; } = Color.FromRgb(0x00, 0x5A, 0x9E);

    public ThemeService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _ui.ColorValuesChanged += (_, _) => _dispatcher.BeginInvoke(Apply);
        Apply();
    }

    private static Color C(WinColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public void Apply()
    {
        try
        {
            var bg = _ui.GetColorValue(UIColorType.Background);
            IsDark = bg.R + bg.G + bg.B < 384;
            var accent = C(_ui.GetColorValue(UIColorType.Accent));
            var light1 = C(_ui.GetColorValue(UIColorType.AccentLight1));
            var light2 = C(_ui.GetColorValue(UIColorType.AccentLight2));
            var dark1 = C(_ui.GetColorValue(UIColorType.AccentDark1));
            Accent = IsDark ? light1 : dark1;
            AccentLight = IsDark ? light2 : accent;
            AccentDark = IsDark ? accent : C(_ui.GetColorValue(UIColorType.AccentDark2));
        }
        catch { /* keep defaults */ }

        var r = Application.Current.Resources;
        Color fg = IsDark ? Colors.White : Colors.Black;
        Color A(byte a) => Color.FromArgb(a, fg.R, fg.G, fg.B);

        r["AccentColor"] = Accent;
        r["AccentLightColor"] = AccentLight;
        r["AccentBrush"] = Frozen(Accent);
        r["AccentLightBrush"] = Frozen(AccentLight);
        r["AccentDarkBrush"] = Frozen(AccentDark);
        r["AccentSoftBrush"] = Frozen(Color.FromArgb(0x40, Accent.R, Accent.G, Accent.B));
        r["OnAccentBrush"] = Frozen(Luma(Accent) > 0.6 ? Colors.Black : Colors.White);

        r["TextPrimaryBrush"] = Frozen(A(0xF2));
        r["TextSecondaryBrush"] = Frozen(A(0xB0));
        r["TextTertiaryBrush"] = Frozen(A(0x78));
        r["SurfaceBrush"] = Frozen(A(0x10));
        r["SurfaceHoverBrush"] = Frozen(A(0x1C));
        r["SurfacePressedBrush"] = Frozen(A(0x0A));
        r["SurfaceStrongBrush"] = Frozen(A(0x2A));
        r["StrokeBrush"] = Frozen(A(0x22));
        r["PlayButtonBrush"] = Frozen(IsDark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));
        r["PlayButtonGlyphBrush"] = Frozen(IsDark ? Color.FromRgb(0x10, 0x10, 0x10) : Colors.White);
        r["OverlayBrush"] = Frozen(IsDark ? Color.FromArgb(0xB8, 0x14, 0x14, 0x18) : Color.FromArgb(0xC8, 0xF4, 0xF4, 0xF6));
        r["MenuBrush"] = Frozen(IsDark ? Color.FromArgb(0xF4, 0x26, 0x26, 0x2A) : Color.FromArgb(0xF8, 0xF9, 0xF9, 0xFB));
        r["MenuStrokeBrush"] = Frozen(IsDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0, 0, 0));
        r["ShelfBrush"] = Frozen(A(0x18));
        // A faint tint over the translucent backdrop keeps text readable over any wallpaper/window.
        r["ScrimBrush"] = Frozen(IsDark ? Color.FromArgb(0x60, 0x0A, 0x0A, 0x0E) : Color.FromArgb(0x60, 0xFA, 0xFA, 0xFC));

        ThemeChanged?.Invoke();
    }

    private static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
