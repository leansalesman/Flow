using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.ViewManagement;
using WinColor = Windows.UI.Color;

namespace Flow.Services;

/// <summary>The selectable looks (Settings → Appearance).</summary>
public enum AppTheme
{
    /// <summary>Translucent: WindHawk's backdrop shows through. Light/dark follows Windows.</summary>
    Minimal,
    Dark,
    Light,
    /// <summary>Solid Dark or Light, following Windows' app mode.</summary>
    Auto,
    /// <summary>Brushed aluminum, LCD display and glossy buttons, in the spirit of mid-2000s iTunes.</summary>
    Metal,
    /// <summary>Brushed Metal in graphite: dark metal, a dark inset panel, a backlit display and dark glossy buttons.</summary>
    DarkMetal,
}

/// <summary>
/// Publishes the active theme's brushes into Application resources (always use DynamicResource, so switching
/// themes restyles everything live). Follows the Windows accent color and light/dark app mode.
/// </summary>
public sealed class ThemeService
{
    private readonly UISettings _ui = new();
    private readonly Dispatcher _dispatcher;

    public event Action? ThemeChanged;

    /// <summary>
    /// Brushes that replace the global ones inside LCD displays (see <see cref="LcdScope"/>). Empty unless the
    /// display's colors differ from the rest of the window (Dark Brushed Metal's pale LCD needs dark ink).
    /// </summary>
    public static Dictionary<string, object> LcdOverrides { get; } = new();
    public static event Action? LcdOverridesChanged;
    public AppTheme Mode { get; private set; }
    public bool IsDark { get; private set; } = true;
    /// <summary>True when the window lets the desktop backdrop show through (Minimal).</summary>
    public bool IsTranslucent => Mode == AppTheme.Minimal;
    public Color Accent { get; private set; } = Color.FromRgb(0x4C, 0xC2, 0xFF);
    public Color AccentLight { get; private set; } = Color.FromRgb(0x99, 0xEB, 0xFF);
    public Color AccentDark { get; private set; } = Color.FromRgb(0x00, 0x5A, 0x9E);

    public ThemeService(Dispatcher dispatcher, AppTheme mode = AppTheme.Minimal)
    {
        _dispatcher = dispatcher;
        Mode = mode;
        _ui.ColorValuesChanged += (_, _) => _dispatcher.BeginInvoke(Apply);
        Apply();
    }

    public static AppTheme Parse(string? name) =>
        Enum.TryParse<AppTheme>(name, true, out var t) ? t : AppTheme.Minimal;

    public void SetMode(AppTheme mode)
    {
        if (mode == Mode) return;
        Mode = mode;
        Apply();
    }

    private static Color C(WinColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public void Apply()
    {
        bool windowsDark = true;
        Color wAccent = Color.FromRgb(0x00, 0x78, 0xD4), wLight1 = Color.FromRgb(0x4C, 0xC2, 0xFF),
              wLight2 = Color.FromRgb(0x99, 0xEB, 0xFF), wDark1 = Color.FromRgb(0x00, 0x5A, 0x9E),
              wDark2 = Color.FromRgb(0x00, 0x42, 0x75);
        try
        {
            var bg = _ui.GetColorValue(UIColorType.Background);
            windowsDark = bg.R + bg.G + bg.B < 384;
            wAccent = C(_ui.GetColorValue(UIColorType.Accent));
            wLight1 = C(_ui.GetColorValue(UIColorType.AccentLight1));
            wLight2 = C(_ui.GetColorValue(UIColorType.AccentLight2));
            wDark1 = C(_ui.GetColorValue(UIColorType.AccentDark1));
            wDark2 = C(_ui.GetColorValue(UIColorType.AccentDark2));
        }
        catch { /* keep defaults */ }

        IsDark = Mode switch
        {
            AppTheme.Dark or AppTheme.DarkMetal => true,
            AppTheme.Light or AppTheme.Metal => false,
            _ => windowsDark,
        };

        if (Mode == AppTheme.Metal)
        {
            // Classic aqua blue rather than the Windows accent.
            Accent = Color.FromRgb(0x2F, 0x74, 0xD6);
            AccentLight = Color.FromRgb(0x6F, 0xA8, 0xEE);
            AccentDark = Color.FromRgb(0x1C, 0x52, 0xA8);
        }
        else if (Mode == AppTheme.DarkMetal)
        {
            // The same aqua blue, brighter so it reads on graphite.
            Accent = Color.FromRgb(0x5A, 0xA2, 0xF5);
            AccentLight = Color.FromRgb(0x9C, 0xC9, 0xFA);
            AccentDark = Color.FromRgb(0x2F, 0x74, 0xD6);
        }
        else
        {
            Accent = IsDark ? wLight1 : wDark1;
            AccentLight = IsDark ? wLight2 : wAccent;
            AccentDark = IsDark ? wAccent : wDark2;
        }

        var r = Application.Current.Resources;
        r["AccentColor"] = Accent;
        r["AccentLightColor"] = AccentLight;
        r["AccentBrush"] = Frozen(Accent);
        r["AccentLightBrush"] = Frozen(AccentLight);
        r["AccentDarkBrush"] = Frozen(AccentDark);
        r["AccentSoftBrush"] = Frozen(Color.FromArgb(0x40, Accent.R, Accent.G, Accent.B));
        r["OnAccentBrush"] = Frozen(Luma(Accent) > 0.6 ? Colors.Black : Colors.White);

        LcdOverrides.Clear();
        if (Mode == AppTheme.Metal) ApplyMetal(r);
        else if (Mode == AppTheme.DarkMetal) ApplyDarkMetal(r);
        else ApplyStandard(r, solid: Mode != AppTheme.Minimal);

        LcdOverridesChanged?.Invoke();
        ThemeChanged?.Invoke();
    }

    // ---- Minimal / Dark / Light ------------------------------------------------------------------

    private void ApplyStandard(ResourceDictionary r, bool solid)
    {
        bool dark = IsDark;
        Color fg = dark ? Colors.White : Colors.Black;
        Color A(byte a) => Color.FromArgb(a, fg.R, fg.G, fg.B);

        r["TextPrimaryBrush"] = Frozen(A(dark ? (byte)0xF2 : (byte)0xE4));
        r["TextSecondaryBrush"] = Frozen(A(dark ? (byte)0xB0 : (byte)0xA0));
        r["TextTertiaryBrush"] = Frozen(A(dark ? (byte)0x78 : (byte)0x70));
        r["SurfaceBrush"] = Frozen(A(0x10));
        r["SurfaceHoverBrush"] = Frozen(A(0x1C));
        r["SurfacePressedBrush"] = Frozen(A(0x0A));
        r["SurfaceStrongBrush"] = Frozen(A(0x2A));
        r["StrokeBrush"] = Frozen(A(0x22));
        r["PlayButtonBrush"] = Frozen(dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));
        r["PlayButtonGlyphBrush"] = Frozen(dark ? Color.FromRgb(0x10, 0x10, 0x10) : Colors.White);
        r["ShelfBrush"] = Frozen(A(0x18));

        if (solid)
        {
            // Standard opaque dark / light (Windows 11 neutrals).
            r["WindowBackgroundBrush"] = Frozen(dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF3, 0xF3, 0xF5));
            r["RailBackgroundBrush"] = Frozen(dark ? Color.FromRgb(0x17, 0x17, 0x19) : Color.FromRgb(0xEA, 0xEA, 0xEE));
            r["OverlayBrush"] = Frozen(dark ? Color.FromArgb(0xF0, 0x26, 0x26, 0x29) : Color.FromArgb(0xF4, 0xFC, 0xFC, 0xFD));
            r["MenuBrush"] = Frozen(dark ? Color.FromRgb(0x2B, 0x2B, 0x2E) : Color.FromRgb(0xFB, 0xFB, 0xFC));
        }
        else
        {
            // A faint tint over the translucent backdrop keeps text readable over any wallpaper/window.
            r["WindowBackgroundBrush"] = Frozen(dark ? Color.FromArgb(0x60, 0x0A, 0x0A, 0x0E) : Color.FromArgb(0x60, 0xFA, 0xFA, 0xFC));
            r["RailBackgroundBrush"] = Brushes.Transparent;
            r["OverlayBrush"] = Frozen(dark ? Color.FromArgb(0xB8, 0x14, 0x14, 0x18) : Color.FromArgb(0xC8, 0xF4, 0xF4, 0xF6));
            r["MenuBrush"] = Frozen(dark ? Color.FromArgb(0xF4, 0x26, 0x26, 0x2A) : Color.FromArgb(0xF8, 0xF9, 0xF9, 0xFB));
        }
        r["ScrimBrush"] = r["WindowBackgroundBrush"];
        r["MenuStrokeBrush"] = Frozen(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0, 0, 0));

        // Slots only Brushed Metal fills in.
        r["WindowShadeBrush"] = Brushes.Transparent;
        r["RailStrokeBrush"] = Brushes.Transparent;
        r["RailPanelMargin"] = new Thickness(0);
        r["RailPanelCornerRadius"] = new CornerRadius(0);
        r["ContentPanelBrush"] = Brushes.Transparent;
        r["ContentHostMargin"] = new Thickness(0);
        r["ContentPanelStrokeBrush"] = Brushes.Transparent;
        r["PlayerBarBrush"] = r["OverlayBrush"];
        r["PlayerBarStrokeBrush"] = r["StrokeBrush"];
        r["LcdBrush"] = r["SurfaceBrush"];
        r["LcdStrokeBrush"] = r["StrokeBrush"];
        r["NowPlayingLcdBrush"] = Brushes.Transparent;
        r["NowPlayingLcdStrokeBrush"] = Brushes.Transparent;
        r["NowPlayingLcdPadding"] = new Thickness(0);
        r["TransportBrush"] = Brushes.Transparent;
        r["TransportStrokeBrush"] = Brushes.Transparent;
        r["PlayButtonStrokeBrush"] = Brushes.Transparent;
        r["RowAltBrush"] = Brushes.Transparent;
        r["RowSelectedBrush"] = r["SurfaceStrongBrush"];
        r["RowCornerRadius"] = new CornerRadius(6);
        r["HeaderBrush"] = Brushes.Transparent;
        r["HeaderStrokeBrush"] = Brushes.Transparent;
        r["ScrollThumbBrush"] = r["SurfaceStrongBrush"];
        r["ArtFrameStrokeBrush"] = Brushes.Transparent;
    }

    // ---- Brushed Metal ---------------------------------------------------------------------------

    private void ApplyMetal(ResourceDictionary r)
    {
        Color Ink(byte a) => Color.FromArgb(a, 0, 0, 0);

        r["TextPrimaryBrush"] = Frozen(Color.FromRgb(0x14, 0x14, 0x16));
        r["TextSecondaryBrush"] = Frozen(Color.FromRgb(0x40, 0x42, 0x46));
        r["TextTertiaryBrush"] = Frozen(Color.FromRgb(0x6A, 0x6C, 0x70));
        r["SurfaceBrush"] = Frozen(Ink(0x0E));
        r["SurfaceHoverBrush"] = Frozen(Ink(0x18));
        r["SurfacePressedBrush"] = Frozen(Ink(0x26));
        r["SurfaceStrongBrush"] = Frozen(Ink(0x24));
        r["StrokeBrush"] = Frozen(Ink(0x40));
        r["ShelfBrush"] = Frozen(Ink(0x12));

        r["WindowBackgroundBrush"] = MetalTexture();
        r["ScrimBrush"] = r["WindowBackgroundBrush"];
        // Light catching the top of the window, darker toward the bottom.
        r["WindowShadeBrush"] = Gradient(90, (0.0, Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), (0.08, Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                                             (0.55, Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)), (1.0, Color.FromArgb(0x38, 0x00, 0x00, 0x00)));

        // Source list: cool blue-gray panel down the left.
        r["RailBackgroundBrush"] = Gradient(0, (0, Color.FromRgb(0xDE, 0xE4, 0xEC)), (1, Color.FromRgb(0xD0, 0xD8, 0xE3)));
        r["RailStrokeBrush"] = Frozen(Color.FromRgb(0x8C, 0x95, 0xA2));
        r["RailPanelMargin"] = new Thickness(8, 46, 6, 14);
        r["RailPanelCornerRadius"] = new CornerRadius(4);

        // The content area is a white, inset panel set into the metal.
        r["ContentPanelBrush"] = Frozen(Color.FromRgb(0xFB, 0xFB, 0xFB));
        r["ContentPanelStrokeBrush"] = Frozen(Color.FromRgb(0x7C, 0x7C, 0x80));
        r["ContentHostMargin"] = new Thickness(0, 16, 18, 4); // keep pages inside the white panel

        r["OverlayBrush"] = Frozen(Color.FromArgb(0xF4, 0xF6, 0xF6, 0xF7));
        r["MenuBrush"] = Frozen(Color.FromRgb(0xF5, 0xF5, 0xF6));
        r["MenuStrokeBrush"] = Frozen(Ink(0x50));

        // Player bar sits straight on the metal; its center is the LCD.
        r["PlayerBarBrush"] = Brushes.Transparent;
        r["PlayerBarStrokeBrush"] = Brushes.Transparent;
        var lcd = Gradient(90, (0, Color.FromRgb(0xF4, 0xF6, 0xE6)), (0.5, Color.FromRgb(0xE8, 0xEC, 0xD3)), (1, Color.FromRgb(0xDA, 0xE0, 0xC0)));
        var lcdStroke = Frozen(Color.FromRgb(0x82, 0x87, 0x70));
        r["LcdBrush"] = lcd;
        r["LcdStrokeBrush"] = lcdStroke;
        r["NowPlayingLcdBrush"] = lcd;
        r["NowPlayingLcdStrokeBrush"] = lcdStroke;
        r["NowPlayingLcdPadding"] = new Thickness(22, 14, 22, 12);

        // Glossy silver buttons: a bright upper half with a sharp highlight edge, like aqua glass.
        var gloss = Gradient(90, (0, Color.FromRgb(0xFF, 0xFF, 0xFF)), (0.46, Color.FromRgb(0xEE, 0xEF, 0xF1)),
                                 (0.5, Color.FromRgb(0xD3, 0xD5, 0xD9)), (1, Color.FromRgb(0xF2, 0xF3, 0xF5)));
        r["PlayButtonBrush"] = gloss;
        r["PlayButtonGlyphBrush"] = Frozen(Color.FromRgb(0x22, 0x22, 0x26));
        r["PlayButtonStrokeBrush"] = Frozen(Color.FromRgb(0x5E, 0x60, 0x66));
        r["TransportBrush"] = gloss;
        r["TransportStrokeBrush"] = Frozen(Color.FromRgb(0x78, 0x7A, 0x80));

        // Track lists: blue-and-white stripes, flat rows, glossy column headers.
        r["RowAltBrush"] = Frozen(Color.FromRgb(0xED, 0xF3, 0xFE));
        r["RowSelectedBrush"] = Frozen(Color.FromRgb(0xB6, 0xCF, 0xF5));
        r["RowCornerRadius"] = new CornerRadius(0);
        r["HeaderBrush"] = Gradient(90, (0, Color.FromRgb(0xFF, 0xFF, 0xFF)), (0.5, Color.FromRgb(0xF0, 0xF0, 0xF0)),
                                        (0.52, Color.FromRgb(0xE4, 0xE4, 0xE4)), (1, Color.FromRgb(0xEE, 0xEE, 0xEE)));
        r["HeaderStrokeBrush"] = Frozen(Color.FromRgb(0xB4, 0xB4, 0xB8));
        r["ScrollThumbBrush"] = Gradient(0, (0, Color.FromRgb(0x9C, 0xBE, 0xEE)), (0.5, Color.FromRgb(0x5C, 0x93, 0xE0)), (1, Color.FromRgb(0x3F, 0x7B, 0xD6)));
        r["ArtFrameStrokeBrush"] = Frozen(Color.FromRgb(0x6A, 0x6A, 0x70));
    }

    // ---- Dark Brushed Metal ----------------------------------------------------------------------

    private void ApplyDarkMetal(ResourceDictionary r)
    {
        Color Ink(byte a) => Color.FromArgb(a, 0xFF, 0xFF, 0xFF);

        r["TextPrimaryBrush"] = Frozen(Color.FromRgb(0xEE, 0xEF, 0xF1));
        r["TextSecondaryBrush"] = Frozen(Color.FromRgb(0xB2, 0xB6, 0xBD));
        r["TextTertiaryBrush"] = Frozen(Color.FromRgb(0x82, 0x87, 0x8F));
        r["SurfaceBrush"] = Frozen(Ink(0x0E));
        r["SurfaceHoverBrush"] = Frozen(Ink(0x1A));
        r["SurfacePressedBrush"] = Frozen(Ink(0x08));
        r["SurfaceStrongBrush"] = Frozen(Ink(0x24));
        r["StrokeBrush"] = Frozen(Ink(0x26));
        r["ShelfBrush"] = Frozen(Ink(0x10));

        r["WindowBackgroundBrush"] = MetalTexture(dark: true);
        r["ScrimBrush"] = r["WindowBackgroundBrush"];
        // A soft sheen across the top edge, falling into shadow toward the bottom.
        r["WindowShadeBrush"] = Gradient(90, (0.0, Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), (0.08, Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)),
                                             (0.55, Color.FromArgb(0x00, 0x00, 0x00, 0x00)), (1.0, Color.FromArgb(0x60, 0x00, 0x00, 0x00)));

        // Source list: a cool slate panel down the left.
        r["RailBackgroundBrush"] = Gradient(0, (0, Color.FromRgb(0x2C, 0x31, 0x39)), (1, Color.FromRgb(0x24, 0x28, 0x2F)));
        r["RailStrokeBrush"] = Frozen(Color.FromRgb(0x0B, 0x0C, 0x0E));
        r["RailPanelMargin"] = new Thickness(8, 46, 6, 14);
        r["RailPanelCornerRadius"] = new CornerRadius(4);

        // The content area is a dark, inset panel set into the metal.
        r["ContentPanelBrush"] = Frozen(Color.FromRgb(0x19, 0x1A, 0x1D));
        r["ContentPanelStrokeBrush"] = Frozen(Color.FromRgb(0x08, 0x08, 0x0A));
        r["ContentHostMargin"] = new Thickness(0, 16, 18, 4);

        r["OverlayBrush"] = Frozen(Color.FromArgb(0xF4, 0x22, 0x24, 0x28));
        r["MenuBrush"] = Frozen(Color.FromRgb(0x27, 0x29, 0x2E));
        r["MenuStrokeBrush"] = Frozen(Ink(0x30));

        // Player bar sits on the metal; its center is the same pale green LCD as Brushed Metal, glowing against
        // the graphite. Inside it, text and controls use Brushed Metal's dark ink.
        r["PlayerBarBrush"] = Brushes.Transparent;
        r["PlayerBarStrokeBrush"] = Brushes.Transparent;
        var lcd = Gradient(90, (0, Color.FromRgb(0xF4, 0xF6, 0xE6)), (0.5, Color.FromRgb(0xE8, 0xEC, 0xD3)), (1, Color.FromRgb(0xDA, 0xE0, 0xC0)));
        var lcdStroke = Frozen(Color.FromRgb(0x05, 0x07, 0x0A));
        Color LcdInk(byte a) => Color.FromArgb(a, 0, 0, 0);
        LcdOverrides["TextPrimaryBrush"] = Frozen(Color.FromRgb(0x14, 0x14, 0x16));
        LcdOverrides["TextSecondaryBrush"] = Frozen(Color.FromRgb(0x40, 0x42, 0x46));
        LcdOverrides["TextTertiaryBrush"] = Frozen(Color.FromRgb(0x6A, 0x6C, 0x70));
        LcdOverrides["SurfaceBrush"] = Frozen(LcdInk(0x0E));
        LcdOverrides["SurfaceHoverBrush"] = Frozen(LcdInk(0x18));
        LcdOverrides["SurfacePressedBrush"] = Frozen(LcdInk(0x26));
        LcdOverrides["SurfaceStrongBrush"] = Frozen(LcdInk(0x24));
        LcdOverrides["StrokeBrush"] = Frozen(LcdInk(0x40));
        LcdOverrides["AccentBrush"] = Frozen(Color.FromRgb(0x2F, 0x74, 0xD6));
        r["LcdBrush"] = lcd;
        r["LcdStrokeBrush"] = lcdStroke;
        r["NowPlayingLcdBrush"] = lcd;
        r["NowPlayingLcdStrokeBrush"] = lcdStroke;
        r["NowPlayingLcdPadding"] = new Thickness(22, 14, 22, 12);

        // Glossy graphite buttons: a lit upper half with a sharp edge, like smoked glass.
        var gloss = Gradient(90, (0, Color.FromRgb(0x72, 0x76, 0x7E)), (0.46, Color.FromRgb(0x4E, 0x52, 0x59)),
                                 (0.5, Color.FromRgb(0x36, 0x39, 0x3F)), (1, Color.FromRgb(0x48, 0x4C, 0x53)));
        r["PlayButtonBrush"] = gloss;
        r["PlayButtonGlyphBrush"] = Frozen(Color.FromRgb(0xF4, 0xF5, 0xF7));
        r["PlayButtonStrokeBrush"] = Frozen(Color.FromRgb(0x0C, 0x0D, 0x0F));
        r["TransportBrush"] = gloss;
        r["TransportStrokeBrush"] = Frozen(Color.FromRgb(0x10, 0x11, 0x13));

        // Track lists: subtle graphite stripes, flat rows, glossy dark column headers.
        r["RowAltBrush"] = Frozen(Color.FromRgb(0x1F, 0x21, 0x25));
        r["RowSelectedBrush"] = Frozen(Color.FromRgb(0x2C, 0x4A, 0x74));
        r["RowCornerRadius"] = new CornerRadius(0);
        r["HeaderBrush"] = Gradient(90, (0, Color.FromRgb(0x3A, 0x3D, 0x43)), (0.5, Color.FromRgb(0x30, 0x33, 0x38)),
                                        (0.52, Color.FromRgb(0x28, 0x2A, 0x2F)), (1, Color.FromRgb(0x2E, 0x31, 0x36)));
        r["HeaderStrokeBrush"] = Frozen(Color.FromRgb(0x0E, 0x0F, 0x11));
        r["ScrollThumbBrush"] = Gradient(0, (0, Color.FromRgb(0x7A, 0xA9, 0xE6)), (0.5, Color.FromRgb(0x4A, 0x82, 0xD2)), (1, Color.FromRgb(0x34, 0x6C, 0xC4)));
        r["ArtFrameStrokeBrush"] = Frozen(Color.FromRgb(0x05, 0x05, 0x06));
    }

    private static Brush? _metalTexture, _darkMetalTexture;


    /// <summary>
    /// Brushed aluminum, generated (no image assets): fine horizontal grain made of noise smeared along
    /// each row, with slight row-to-row variation. Tiles seamlessly. Dark = graphite instead of silver.
    /// </summary>
    private static Brush MetalTexture(bool dark = false)
    {
        if (!dark && _metalTexture != null) return _metalTexture;
        if (dark && _darkMetalTexture != null) return _darkMetalTexture;
        const int w = 512, h = 256;
        var px = new byte[w * h * 4];
        var rnd = new Random(1979);
        var noise = new float[w];
        float rowShade = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) noise[x] = (float)rnd.NextDouble() - 0.5f;
            rowShade = rowShade * 0.55f + ((float)rnd.NextDouble() - 0.5f) * 0.45f;
            // Running average along the row (wrapping) = long horizontal streaks.
            const int k = 20;
            float sum = 0;
            for (int i = -k; i <= k; i++) sum += noise[(i + w) % w];
            for (int x = 0; x < w; x++)
            {
                float streak = sum / (2 * k + 1);
                float v = dark
                    ? 54 + rowShade * 6 + streak * 46 + ((float)rnd.NextDouble() - 0.5f) * 4
                    : 203 + rowShade * 9 + streak * 70 + ((float)rnd.NextDouble() - 0.5f) * 5;
                byte b = (byte)Math.Clamp(v, 0, 255);
                int o = (y * w + x) * 4;
                px[o] = (byte)Math.Min(255, b + 3); // a hint of cool blue
                px[o + 1] = (byte)Math.Min(255, b + 1);
                px[o + 2] = b;
                px[o + 3] = 255;
                sum += noise[(x + k + 1) % w] - noise[(x - k + w) % w];
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        var brush = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            Viewport = new Rect(0, 0, w, h),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return dark ? _darkMetalTexture = brush : _metalTexture = brush;
    }

    /// <summary>A frozen linear gradient; angle 90 = top→bottom, 0 = left→right.</summary>
    private static Brush Gradient(double angle, params (double Offset, Color Color)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = angle == 90 ? new Point(0, 1) : new Point(1, 0) };
        foreach (var (o, c) in stops) g.GradientStops.Add(new GradientStop(c, o));
        g.Freeze();
        return g;
    }

    private static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Set <c>LcdScope.IsLcd="True"</c> on an LCD display: inside it, the theme's <see cref="ThemeService.LcdOverrides"/>
/// replace the global brushes (DynamicResource finds the element's own resources first).
/// </summary>
public static class LcdScope
{
    public static readonly DependencyProperty IsLcdProperty = DependencyProperty.RegisterAttached(
        "IsLcd", typeof(bool), typeof(LcdScope), new PropertyMetadata(false, OnChanged));

    public static bool GetIsLcd(DependencyObject d) => (bool)d.GetValue(IsLcdProperty);
    public static void SetIsLcd(DependencyObject d, bool value) => d.SetValue(IsLcdProperty, value);

    private static readonly List<WeakReference<FrameworkElement>> Scopes = new();

    static LcdScope() => ThemeService.LcdOverridesChanged += () =>
    {
        Scopes.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Scopes) if (w.TryGetTarget(out var e)) Apply(e);
    };

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not true) return;
        Scopes.Add(new WeakReference<FrameworkElement>(fe));
        // Text that doesn't set its own color inherits it; resolve it here so it picks up the LCD's ink.
        fe.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");
        Apply(fe);
    }

    private static readonly string[] Keys =
    {
        "TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush", "SurfaceBrush", "SurfaceHoverBrush",
        "SurfacePressedBrush", "SurfaceStrongBrush", "StrokeBrush", "AccentBrush",
    };

    private static void Apply(FrameworkElement e)
    {
        foreach (var k in Keys)
        {
            if (ThemeService.LcdOverrides.TryGetValue(k, out var v)) e.Resources[k] = v;
            else e.Resources.Remove(k);
        }
    }
}
