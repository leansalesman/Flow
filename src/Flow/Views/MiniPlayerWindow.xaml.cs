using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Flow.Services;
using Flow.ViewModels;

namespace Flow.Views;

/// <summary>
/// The pop-out mini player, floating above all windows anywhere on the desktop (the main window minimizes while
/// it's out). It resizes freely from any edge or corner and lays itself out for the shape it's given: album art
/// with the LCD under it, or, once there isn't room for a decent cover, just the LCD as a bar whose text, timeline
/// and buttons adapt to its size. Controls appear on hover and fade a few seconds after the pointer leaves.
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private const double Inset = 16;          // shadow margin around the player (8 each side)
    private const double MinArt = 120;        // smaller than this, the art goes and the LCD becomes a bar
    private const double MinW = 140, MinH = 40; // the player itself (without the shadow margin)
    private const double Gap = 6;
    private static MiniPlayerWindow? _current;

    private readonly MainViewModel _vm;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _hide;
    private bool _shown, _idleHide, _barMode;

    public static bool IsOpen => _current != null;

#if DEBUG
    // --render-library: open off-screen and drive it for snapshots.
    internal static bool ProbeMode;
    internal static MiniPlayerWindow? Current => _current;
    internal void ProbeShowControls(bool show) => SetControls(show);
    internal void ProbeTogglePanel() => PanelToggle_Click(this, new RoutedEventArgs());
    internal void ProbeSetPanel(bool on) { if (_panelOn != on) PanelToggle_Click(this, new RoutedEventArgs()); }
    internal void ProbeSetSize(double w, double h) { Width = w + Inset; Height = h + Inset; }
#endif

    /// <summary>Pops the mini player out (minimizing Flow), or closes it when it's already open.</summary>
    public static void Toggle(MainViewModel vm, SettingsService settings)
    {
        if (_current != null) { _current.Close(); return; }
        _current = new MiniPlayerWindow(vm, settings);
        _current.Show();
#if DEBUG
        if (ProbeMode) return;
#endif
        // Only the mini player on screen until the user wants Flow back.
        if (Application.Current.MainWindow is MainWindow main && main.IsVisible) main.WindowState = WindowState.Minimized;
    }

    private MiniPlayerWindow(MainViewModel vm, SettingsService settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        _hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hide.Tick += (_, _) => { _hide.Stop(); if (!IsMouseOver || _idleHide) SetControls(false); _idleHide = false; };

        var s = settings.Current;
        _panelOn = s.MiniPlayerSongPanel;
        UpdatePanelToggle();
        Width = Math.Max(MinW + Inset, s.MiniPlayerWidth);
        Height = Math.Max(MinH + Inset, s.MiniPlayerHeight ?? 388);
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Width = Math.Min(Width, work.Width);
            Height = Math.Min(Height, work.Height);
            PlaceOnScreen(s.MiniPlayerLeft, s.MiniPlayerTop);
        };

        MouseEnter += (_, _) => SetControls(true);
        MouseMove += (_, _) => { SetControls(true); _idleHide = true; _hide.Stop(); _hide.Start(); };
        MouseLeave += (_, _) => { _idleHide = false; _hide.Stop(); _hide.Start(); };
        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            _hide.Stop();
            SaveBounds();
            _current = null;
            // Closing the mini player brings Flow back (unless Flow itself is closing).
            if (!Application.Current.Dispatcher.HasShutdownStarted && Application.Current.MainWindow is MainWindow main
                && main.WindowState == WindowState.Minimized)
                main.BringToFront();
        };
    }

    // ---- Layout: art + LCD, or the LCD alone as a bar ----

    private bool _panelOn = true;

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => Arrange();

    private void Arrange()
    {
        double w = Stage.ActualWidth, h = Stage.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double lcdH = _panelOn ? Math.Clamp(w * 0.18, 46, 74) : 0;
        double art = Math.Min(w, h - (_panelOn ? lcdH + Gap : 0));
        bool bar = art < MinArt;
        if (bar != _barMode)
        {
            _barMode = bar;
            SetControls(_shown, force: true);   // hover now shows the other set of controls
        }

        if (bar)
        {
            ArtBox.Visibility = Visibility.Collapsed;
            SongPanel.Visibility = Visibility.Visible;
            SongPanel.Margin = new Thickness(4);
            SongPanel.Height = Math.Max(0, h - 8);
            SongPanel.Width = Math.Max(0, w - 8);
            LayoutLcd(w - 8, h - 8, bar: true);
            return;
        }

        // Art centered, LCD under it; the pair is centered vertically when the player is taller than it needs.
        double total = art + (_panelOn ? Gap + lcdH : 0);
        double top = Math.Max(0, (h - total) / 2);
        ArtBox.Visibility = Visibility.Visible;
        ArtBox.Width = ArtBox.Height = art;
        ArtBox.Margin = new Thickness(0, top, 0, 0);
        SongPanel.Visibility = _panelOn ? Visibility.Visible : Visibility.Collapsed;
        if (_panelOn)
        {
            double lcdW = Math.Max(art, Math.Min(w, art + 80)) - 12;
            SongPanel.Width = lcdW;
            SongPanel.Height = lcdH;
            SongPanel.Margin = new Thickness((w - lcdW) / 2, top + art + Gap, 0, 0);
            SongPanel.HorizontalAlignment = HorizontalAlignment.Left;
            LayoutLcd(lcdW, lcdH, bar: false);
        }

        // The art's own controls: extras only when the cover is big enough to hold them comfortably.
        var extras = art < 236 ? Visibility.Collapsed : Visibility.Visible;
        ShuffleToggle.Visibility = RepeatToggle.Visibility = SecondaryRow.Visibility = extras;
        TimelineRow.Visibility = art < 176 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The LCD fits its text to its size: a bigger title in a taller bar, the second line only when there's
    /// height for it, the timeline (bar only) when there's room, and fewer buttons when it's narrow.
    /// </summary>
    private void LayoutLcd(double w, double h, bool bar)
    {
        SongPanel.HorizontalAlignment = bar ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        double title = bar ? Math.Clamp(h * 0.24, 11, 22) : Math.Clamp(h * 0.26, 12, 16);
        LcdTitle.FontSize = title;
        double small = Math.Max(9, title * 0.74);
        LcdSubAlbum.FontSize = LcdSubSpotify.FontSize = LcdElapsed.FontSize = LcdDuration.FontSize = small;

        bool showTimeline = bar && h >= 44 && w >= 200;
        bool showSub = bar ? h >= (showTimeline ? 70 : 46) && w >= 170 : h >= 44;
        LcdSub.Visibility = showSub ? Visibility.Visible : Visibility.Collapsed;
        LcdTimeline.Visibility = showTimeline ? Visibility.Visible : Visibility.Collapsed;

        // Bar buttons scale with its height; narrow bars keep just play/pause (then previous/next, then the rest).
        double b = Math.Clamp(h * 0.62, 24, 40);
        foreach (var c in new Control[] { LcdPrev, LcdPlay, LcdNext, LcdShuffle, LcdRepeat, LcdFavorite, LcdMute, LcdBack, LcdClose })
        {
            c.Width = c.Height = b;
            c.FontSize = Math.Clamp(b * 0.4, 10, 16);
        }
        LcdPrev.Visibility = LcdNext.Visibility = w >= 250 ? Visibility.Visible : Visibility.Collapsed;
        LcdExtras.Visibility = w >= 660 ? Visibility.Visible : Visibility.Collapsed;
        LcdBack.Visibility = w >= 190 ? Visibility.Visible : Visibility.Collapsed;
        ApplyLcdHover();
    }

    // ---- Controls fade in on hover, out a few seconds after the pointer leaves ----

    private void SetControls(bool show, bool force = false)
    {
        if (show == _shown && !force) return;
        _shown = show;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 140 : 380)) { EasingFunction = ease };
        bool art = show && !_barMode, lcd = show && _barMode;
        Controls.IsHitTestVisible = art;
        LcdControls.IsHitTestVisible = lcd;
        Controls.BeginAnimation(OpacityProperty, new DoubleAnimation(art ? 1 : 0, fade.Duration) { EasingFunction = ease });
        LcdControls.BeginAnimation(OpacityProperty, new DoubleAnimation(lcd ? 1 : 0, fade.Duration) { EasingFunction = ease });
        ApplyLcdHover();
    }

    /// <summary>While the bar's buttons show, the text makes room for them (or steps back when there isn't any).</summary>
    private void ApplyLcdHover()
    {
        if (!_barMode || !_shown)
        {
            LcdInfo.Margin = new Thickness(10, 4, 10, 4);
            LcdInfo.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            return;
        }
        LcdLeft.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        LcdRight.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = LcdLeft.DesiredSize.Width + 10, right = LcdRight.DesiredSize.Width + 10;
        double room = SongPanel.ActualWidth - left - right;
        if (room >= 110)
        {
            LcdInfo.Margin = new Thickness(left, 4, right, 4);
            LcdInfo.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        }
        else
        {
            LcdInfo.Margin = new Thickness(10, 4, 10, 4);
            LcdInfo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));   // buttons only
        }
    }

    // ---- Moving, resizing, song panel ----

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { BackToFlow_Click(sender, e); return; }
        try { DragMove(); } catch { }
        SaveBounds();
    }

    private const int WM_NCHITTEST = 0x0084, WM_SIZING = 0x0214, WM_EXITSIZEMOVE = 0x0232;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                      HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int WMSZ_LEFT = 1, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5, WMSZ_BOTTOMLEFT = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                // The shadow margin (plus a few pixels inside the edge) is the resize border.
                int sx = unchecked((short)(long)lParam), sy = unchecked((short)((long)lParam >> 16));
                var p = PointFromScreen(new Point(sx, sy));
                double band = 11;
                bool left = p.X < band, right = p.X > ActualWidth - band, top = p.Y < band, bottom = p.Y > ActualHeight - band;
                int hit = top && left ? HTTOPLEFT : top && right ? HTTOPRIGHT : bottom && left ? HTBOTTOMLEFT
                        : bottom && right ? HTBOTTOMRIGHT : left ? HTLEFT : right ? HTRIGHT : top ? HTTOP : bottom ? HTBOTTOM : 0;
                if (hit != 0) { handled = true; return new IntPtr(hit); }
                break;
            }
            case WM_SIZING:
            {
                // Any shape, down to a small bar; never larger than the screen.
                var r = Marshal.PtrToStructure<RECT>(lParam);
                double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
                var work = SystemParameters.WorkArea;
                int minW = (int)((MinW + Inset) * dpi), minH = (int)((MinH + Inset) * dpi);
                int maxW = (int)(work.Width * dpi), maxH = (int)(work.Height * dpi);
                int edge = wParam.ToInt32();
                int w = Math.Clamp(r.Right - r.Left, minW, maxW), h = Math.Clamp(r.Bottom - r.Top, minH, maxH);
                if (edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT) r.Left = r.Right - w; else r.Right = r.Left + w;
                if (edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT) r.Top = r.Bottom - h; else r.Bottom = r.Top + h;
                Marshal.StructureToPtr(r, lParam, false);
                handled = true;
                return new IntPtr(1);
            }
            case WM_EXITSIZEMOVE:
                SaveBounds();
                break;
        }
        return IntPtr.Zero;
    }

    private void PanelToggle_Click(object sender, RoutedEventArgs e)
    {
        _panelOn = !_panelOn;
        _settings.Current.MiniPlayerSongPanel = _panelOn;
        UpdatePanelToggle();
        Arrange();
        SaveBounds();
    }

    private void UpdatePanelToggle()
    {
        PanelToggle.Content = _panelOn ? "" : "";
        PanelToggle.ToolTip = _panelOn ? "Hide the song panel" : "Show the song panel";
    }

    /// <summary>Back where it was last time, or the bottom-right of the screen; always fully visible.</summary>
    private void PlaceOnScreen(double? left, double? top)
    {
        var work = SystemParameters.WorkArea;
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
#if DEBUG
        if (ProbeMode) { Left = -30000; Top = -30000; return; }
#endif
        if (left is double l && top is double t && l >= vl - 40 && t >= vt - 20 && l + 80 <= vl + vw && t + 40 <= vh + vt)
        {
            Left = l;
            Top = t;
            return;
        }
        Left = work.Right - Width - 16;
        Top = work.Bottom - Height - 16;
    }

    private void SaveBounds()
    {
        var s = _settings.Current;
        s.MiniPlayerLeft = Left;
        s.MiniPlayerTop = Top;
        s.MiniPlayerWidth = Width;
        s.MiniPlayerHeight = Height;
        _settings.Save();
    }

    // ---- Buttons ----

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void BackToFlow_Click(object sender, RoutedEventArgs e)
    {
        (Application.Current.MainWindow as MainWindow)?.BringToFront();
        Close();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => _vm.Playback.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => _vm.Playback.Next();

    private void QueueToggle_Click(object sender, RoutedEventArgs e) =>
        QueuePanel.Visibility = QueueToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void QueueClose_Click(object sender, RoutedEventArgs e)
    {
        QueueToggle.IsChecked = false;
        QueuePanel.Visibility = Visibility.Collapsed;
    }

    private void QueueList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var c = DragReorder.FindContainer(e.OriginalSource as DependencyObject);
        if (c != null) _vm.Playback.PlayAt(QueueList.ItemContainerGenerator.IndexFromContainer(c));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: _vm.Playback.PlayPause(); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
        }
    }

    // ---- Timeline (on the art, or in the bar) ----

    private void Timeline_Down(object sender, MouseButtonEventArgs e) => _vm.Playback.IsUserSeeking = true;

    private void Timeline_Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider s) _vm.Playback.Seek(s.Value);
        _vm.Playback.IsUserSeeking = false;
    }

    private void Timeline_DragStarted(object sender, DragStartedEventArgs e) => _vm.Playback.IsUserSeeking = true;

    private void Timeline_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Slider s) _vm.Playback.Seek(s.Value);
        _vm.Playback.IsUserSeeking = false;
    }
}
