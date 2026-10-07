using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Flow.Interop;
using Flow.Services;
using Flow.ViewModels;
using static Flow.Interop.NativeMethods;

namespace Flow;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly DispatcherTimer _idleTimer;
    private SmtcService? _smtc;
    private TrayIcon? _tray;
    private bool _chromeHidden;
    private bool _reallyClose;
    private bool _maxHover;

    /// <summary>True for the off-screen memory probe: no saved bounds, media controls or tray icon.</summary>
    internal static bool ProbeMode;

    public MainWindow(MainViewModel vm, SettingsService settings, ThemeService theme)
    {
        _vm = vm;
        _settings = settings;
        _theme = theme;
        InitializeComponent();
        DataContext = vm;

        if (!ProbeMode) RestoreBounds_();

        var thumbs = TaskbarItemInfo.ThumbButtonInfos;
        thumbs[0].Command = vm.PreviousCommand;
        thumbs[1].Command = vm.PlayPauseCommand;
        thumbs[2].Command = vm.NextCommand;

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); UpdateChromeFade(idle: true); };

        vm.PropertyChanged += OnVmChanged;
        vm.Playback.PropertyChanged += OnPlaybackChanged;
        vm.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.MinimizeToTray) && _tray != null)
                _tray.Visible = _settings.Current.MinimizeToTray;
        };
        theme.ThemeChanged += () => WindowEffects.SetDarkMode(this, _theme.IsDark);

        StateChanged += (_, _) => OnStateChanged();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseMove += (_, _) => OnUserActivity();
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        Closing += OnClosing;

        UpdateMaxGlyph();
    }

    // ---- Window setup --------------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.Apply(this, _theme.IsDark);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        if (ProbeMode) return;
        _smtc = new SmtcService(hwnd, _vm.Playback, Dispatcher);

        _tray = new TrayIcon { ToolTip = "Flow" };
        _tray.Activate += BringToFront;
        _tray.MenuProvider = () => new List<(int, string)>
        {
            (1, "Show Flow"),
            (0, ""),
            (2, _vm.Playback.IsPlaying ? "Pause" : "Play"),
            (3, "Next"),
            (4, "Previous"),
            (0, ""),
            (9, "Exit"),
        };
        _tray.MenuCommand += id => Dispatcher.BeginInvoke(() =>
        {
            switch (id)
            {
                case 1: BringToFront(); break;
                case 2: _vm.Playback.PlayPause(); break;
                case 3: _vm.Playback.Next(); break;
                case 4: _vm.Playback.Previous(); break;
                case 9: ExitApp(); break;
            }
        });
        _tray.Visible = _settings.Current.MinimizeToTray;
    }

    private void RestoreBounds_()
    {
        var s = _settings.Current;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        bool onScreen = !double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop)
            && s.WindowLeft > SystemParameters.VirtualScreenLeft - 50
            && s.WindowTop > SystemParameters.VirtualScreenTop - 50
            && s.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
            && s.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;
        if (onScreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (s.WindowMaximized) Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private void SaveBounds()
    {
        var s = _settings.Current;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!b.IsEmpty && b.Width > 0)
        {
            s.WindowLeft = b.Left;
            s.WindowTop = b.Top;
            s.WindowWidth = b.Width;
            s.WindowHeight = b.Height;
        }
    }

    private void OnStateChanged()
    {
        if (WindowState == WindowState.Maximized)
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double frame = (GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER)) / scale;
            RootGrid.Margin = new Thickness(frame);
        }
        else RootGrid.Margin = new Thickness(0);
        UpdateMaxGlyph();
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray)
        {
            _tray!.Visible = true;
            Hide();
        }
        if (WindowState == WindowState.Minimized)
            Flow.Infrastructure.MemoryTrim.Request(Dispatcher, trimWorkingSet: true, force: true);
    }

    private void UpdateMaxGlyph()
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ---- Snap Layouts on the custom maximize button ---------------------------------------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (IsOverMaxButton(lParam))
                {
                    SetMaxHover(true);
                    handled = true;
                    return new IntPtr(HTMAXBUTTON);
                }
                SetMaxHover(false);
                break;
            case WM_NCMOUSELEAVE:
                SetMaxHover(false);
                break;
            case WM_NCLBUTTONDOWN:
            case WM_NCLBUTTONDBLCLK:
                if (wParam.ToInt32() == HTMAXBUTTON) { handled = true; return IntPtr.Zero; }
                break;
            case WM_NCLBUTTONUP:
                if (wParam.ToInt32() == HTMAXBUTTON)
                {
                    handled = true;
                    ToggleMaximize();
                    return IntPtr.Zero;
                }
                break;
            case WM_DWMCOMPOSITIONCHANGED:
                WindowEffects.ExtendFrame(hwnd);
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaxButton(IntPtr lParam)
    {
        if (!MaxButton.IsVisible) return false;
        long lp = lParam.ToInt64();
        int x = unchecked((short)(lp & 0xFFFF));
        int y = unchecked((short)((lp >> 16) & 0xFFFF));
        try
        {
            var p = MaxButton.PointFromScreen(new Point(x, y));
            return p.X >= 0 && p.Y >= 0 && p.X < MaxButton.ActualWidth && p.Y < MaxButton.ActualHeight;
        }
        catch { return false; }
    }

    private void SetMaxHover(bool on)
    {
        if (_maxHover == on) return;
        _maxHover = on;
        if (on) MaxButton.SetResourceReference(BackgroundProperty, "SurfaceHoverBrush");
        else MaxButton.ClearValue(BackgroundProperty);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Close / exit --------------------------------------------------------------------------

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_reallyClose && _settings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            _tray!.Visible = true;
            Hide();
            Flow.Infrastructure.MemoryTrim.Request(Dispatcher, trimWorkingSet: true, force: true);
            return;
        }
        SaveBounds();
        ((App)Application.Current).SaveAll();
        _smtc?.Dispose();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    private void ExitApp()
    {
        _reallyClose = true;
        Close();
    }

    // ---- View model reactions -----------------------------------------------------------------

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage)) ShowPage(_vm.CurrentPage);
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        var pb = _vm.Playback;
        switch (e.PropertyName)
        {
            case nameof(PlaybackService.IsPlaying):
                var thumb = TaskbarItemInfo.ThumbButtonInfos[1];
                thumb.ImageSource = (ImageSource)FindResource(pb.IsPlaying ? "ThumbPause" : "ThumbPlay");
                thumb.Description = pb.IsPlaying ? "Pause" : "Play";
                TaskbarItemInfo.ProgressState = pb.HasTrack ? (pb.IsPlaying ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Paused)
                                                            : TaskbarItemProgressState.None;
                UpdateChromeFade(idle: false);
                if (pb.IsPlaying) OnUserActivity();
                break;
            case nameof(PlaybackService.CurrentTrack):
                var t = pb.CurrentTrack;
                Title = t == null ? "Flow" : $"{t.Title} — {t.DisplayArtist} · Flow";
                if (_tray != null) _tray.ToolTip = t == null ? "Flow" : $"{t.Title}\n{t.DisplayArtist}";
                if (t == null) TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
                break;
            case nameof(PlaybackService.PositionSeconds):
                if (pb.DurationSeconds > 0) TaskbarItemInfo.ProgressValue = Math.Clamp(pb.PositionSeconds / pb.DurationSeconds, 0, 1);
                break;
        }
    }

    private FrameworkElement PageElement(AppPage p) => p switch
    {
        AppPage.Library => LibraryPage,
        AppPage.Playlists => PlaylistsPage,
        AppPage.Settings => SettingsPage,
        _ => NowPlayingPage,
    };

    private AppPage _shownPage = AppPage.NowPlaying;

    /// <summary>
    /// Builds the Library and Playlists screens in the background once startup has settled, so the first
    /// visit doesn't stall while hundreds of album tiles are created. Hidden pages get laid out but not drawn.
    /// </summary>
    public void WarmUpPages()
    {
        foreach (var page in new FrameworkElement[] { LibraryPage, PlaylistsPage })
        {
            if (page.Visibility != Visibility.Collapsed) continue;
            page.Visibility = Visibility.Hidden;
            page.UpdateLayout();
            if (page.Visibility == Visibility.Hidden) page.Visibility = Visibility.Collapsed;
        }
    }

    private DispatcherTimer? _trimTimer;

    private void ShowPage(AppPage page)
    {
        // Leaving the Library: release covers that are no longer on screen — a few seconds later, so the
        // collection doesn't land in the middle of the transition (and is skipped if we come straight back).
        if (_shownPage == AppPage.Library && page != AppPage.Library)
        {
            _trimTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.ApplicationIdle, (_, _) =>
            {
                _trimTimer!.Stop();
                if (_shownPage != AppPage.Library) Flow.Infrastructure.MemoryTrim.Request(Dispatcher);
            }, Dispatcher);
            _trimTimer.Stop();
            _trimTimer.Start();
        }
        _shownPage = page;
        var target = PageElement(page);
        foreach (FrameworkElement child in ContentHost.Children)
        {
            if (ReferenceEquals(child, target)) continue;
            child.Visibility = Visibility.Collapsed;
        }
        target.Visibility = Visibility.Visible;
        target.Opacity = 0;
        var tt = new TranslateTransform(0, 10);
        target.RenderTransform = tt;
        // While it fades in, a page is drawn once into a cached bitmap that is then just blended, instead of being
        // re-rendered on every animation frame. Not for the Library: covers stream in (and the album panel may be
        // sliding in) during the fade, and each change would re-render the whole cached page.
        if (page != AppPage.Library) target.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        var slide = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        slide.Completed += (_, _) => target.CacheMode = null;
        target.BeginAnimation(OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.YProperty, slide);
        if (page == AppPage.Playlists) _vm.Playlists.LoadTracks();
        UpdateChromeFade(idle: false);
        OnUserActivity();
    }

    // ---- Fade the rail + title bar while music plays and the mouse is idle ---------------------

    private void OnUserActivity()
    {
        if (_chromeHidden) UpdateChromeFade(idle: false);
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void UpdateChromeFade(bool idle)
    {
        bool hide = idle && _settings.Current.FadeChromeWhilePlaying && _vm.Playback.IsPlaying
                    && _vm.CurrentPage == AppPage.NowPlaying && !_vm.IsQueueOpen
                    && !Rail.IsMouseOver && !TitleBar.IsMouseOver;
        if (hide == _chromeHidden) return;
        _chromeHidden = hide;
        var anim = new DoubleAnimation(hide ? 0 : 1, TimeSpan.FromMilliseconds(hide ? 700 : 180))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Rail.BeginAnimation(OpacityProperty, anim);
        TitleBar.BeginAnimation(OpacityProperty, anim);
        NowPlayingPage.SetChromeHidden(hide);
    }

    // ---- Keyboard --------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        OnUserActivity();
        var focused = Keyboard.FocusedElement as DependencyObject;
        bool inText = focused is TextBox;
        bool inList = focused is ListBoxItem or DataGridCell or DataGridRow or ListBox or DataGrid or Slider;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var pb = _vm.Playback;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.F:
                    _vm.CurrentPage = AppPage.Library;
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () => LibraryPage.FocusSearch());
                    e.Handled = true; return;
                case Key.L: _vm.CurrentPage = AppPage.Library; e.Handled = true; return;
                case Key.P: _vm.CurrentPage = AppPage.NowPlaying; e.Handled = true; return;
                case Key.O: _vm.OpenFilesCommand.Execute(null); e.Handled = true; return;
                case Key.Right: if (!inText) { pb.Next(); e.Handled = true; } return;
                case Key.Left: if (!inText) { pb.Previous(); e.Handled = true; } return;
                case Key.S: if (!inText) { pb.Shuffle = !pb.Shuffle; e.Handled = true; } return;
                case Key.R: if (!inText) { pb.CycleRepeat(); e.Handled = true; } return;
            }
        }

        if (inText)
        {
            if (e.Key == Key.Escape) { Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this, this); e.Handled = true; }
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                pb.PlayPause(); e.Handled = true; break;
            case Key.MediaPlayPause:
            case Key.MediaNextTrack:
            case Key.MediaPreviousTrack:
                break; // handled globally by the system media controls
            case Key.Left when !inList:
                pb.SeekRelative(-5); e.Handled = true; break;
            case Key.Right when !inList:
                pb.SeekRelative(5); e.Handled = true; break;
            case Key.Up when !inList:
                pb.Volume += 0.05; e.Handled = true; break;
            case Key.Down when !inList:
                pb.Volume -= 0.05; e.Handled = true; break;
            case Key.M:
                pb.IsMuted = !pb.IsMuted; e.Handled = true; break;
            case Key.Escape:
                if (_vm.IsQueueOpen) _vm.IsQueueOpen = false;
                else if (_vm.Library.IsAlbumOpen) _vm.Library.SelectedAlbum = null;      // back to the artist page, if any
                else if (_vm.Library.IsArtistOpen) _vm.Library.SelectedArtist = null;
                e.Handled = true; break;
        }
    }

    // ---- Drag & drop -----------------------------------------------------------------------------

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; // internal drags (list reordering)
        DropOverlay.Visibility = Visibility.Visible;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            bool append = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            _vm.Playback.PlayFiles(paths, append);
            if (!append) _vm.CurrentPage = AppPage.NowPlaying;
            e.Handled = true;
        }
    }
}
