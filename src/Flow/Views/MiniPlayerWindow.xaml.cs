using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Flow.Services;
using Flow.ViewModels;

namespace Flow.Views;

/// <summary>
/// The pop-out mini player: album art that floats above all windows, anywhere on the desktop. Hovering shows
/// every control (transport, shuffle, repeat, favorite, Up Next, volume, timeline); they fade away a few seconds
/// after the pointer leaves. Resizing keeps its shape, so everything scales evenly; the song panel under the art
/// can be hidden for art only.
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private const double Inset = 16;      // shadow margin around the player (8 each side)
    private const double DesignWidth = 300;
    private static MiniPlayerWindow? _current;

    private readonly MainViewModel _vm;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _hide;
    private bool _shown;

    public static bool IsOpen => _current != null;

#if DEBUG
    // --render-library: open off-screen and drive it for snapshots.
    internal static bool ProbeMode;
    internal static MiniPlayerWindow? Current => _current;
    internal void ProbeShowControls(bool show) => SetControls(show);
    internal void ProbeTogglePanel() => PanelToggle_Click(this, new RoutedEventArgs());
#endif

    /// <summary>Pops the mini player out, or closes it when it's already open.</summary>
    public static void Toggle(MainViewModel vm, SettingsService settings)
    {
        if (_current != null) { _current.Close(); return; }
        _current = new MiniPlayerWindow(vm, settings);
        _current.Show();
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
        SongPanel.Visibility = s.MiniPlayerSongPanel ? Visibility.Visible : Visibility.Collapsed;
        UpdatePanelToggle();
        Width = Math.Clamp(s.MiniPlayerWidth, 220, 720);
        Loaded += (_, _) =>
        {
            FitHeight();
            PlaceOnScreen(s.MiniPlayerLeft, s.MiniPlayerTop);
        };

        MouseEnter += (_, _) => SetControls(true);
        MouseMove += (_, _) => { SetControls(true); _idleHide = true; _hide.Stop(); _hide.Start(); };
        MouseLeave += (_, _) => { _idleHide = false; _hide.Stop(); _hide.Interval = TimeSpan.FromSeconds(2.5); _hide.Start(); };
        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            _hide.Stop();
            SaveBounds();
            _current = null;
        };
    }

    private bool _idleHide;

    // ---- Controls fade in on hover, out a few seconds after the pointer leaves ----

    private void SetControls(bool show)
    {
        if (show == _shown) return;
        _shown = show;
        Controls.IsHitTestVisible = show;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 140 : 380)) { EasingFunction = ease };
        Controls.BeginAnimation(OpacityProperty, fade);
        Grip.BeginAnimation(OpacityProperty, fade.Clone());
    }

    // ---- Moving, resizing, song panel ----

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { BackToFlow_Click(sender, e); return; }
        try { DragMove(); } catch { }
        SaveBounds();
    }

    /// <summary>The player's height at 300 wide (art plus the song panel when shown).</summary>
    private double DesignHeight()
    {
        Frame.Measure(new Size(DesignWidth, double.PositiveInfinity));
        return Math.Max(DesignWidth, Frame.DesiredSize.Height);
    }

    private void FitHeight() => Height = (Width - Inset) * DesignHeight() / DesignWidth + Inset;

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double ratio = DesignHeight() / DesignWidth;
        // Follow whichever way the pointer moved more, keeping the shape.
        double grow = Math.Abs(e.HorizontalChange) >= Math.Abs(e.VerticalChange * ratio) ? e.HorizontalChange : e.VerticalChange / ratio;
        Width = Math.Clamp(Width + grow, 220, 720);
        FitHeight();
    }

    private void Grip_DragCompleted(object sender, DragCompletedEventArgs e) => SaveBounds();

    private void PanelToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = SongPanel.Visibility != Visibility.Visible;
        SongPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _settings.Current.MiniPlayerSongPanel = show;
        UpdatePanelToggle();
        FitHeight();
        SaveBounds();
    }

    private void UpdatePanelToggle()
    {
        bool shown = SongPanel.Visibility == Visibility.Visible;
        PanelToggle.Content = shown ? "" : "";
        PanelToggle.ToolTip = shown ? "Hide the song panel" : "Show the song panel";
    }

    /// <summary>Back where it was last time, or the bottom-right of the screen; always fully visible.</summary>
    private void PlaceOnScreen(double? left, double? top)
    {
        var work = SystemParameters.WorkArea;
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
#if DEBUG
        if (ProbeMode) left = null;
#endif
        if (left is double l && top is double t && l >= vl - 40 && t >= vt - 20 && l + 80 <= vl + vw && t + 80 <= vh + vt)
        {
            Left = l;
            Top = t;
            return;
        }
        Left = work.Right - Width - 16;
        Top = work.Bottom - Height - 16;
#if DEBUG
        if (ProbeMode) { Left = -30000; Top = -30000; }
#endif
    }

    private void SaveBounds()
    {
        var s = _settings.Current;
        s.MiniPlayerLeft = Left;
        s.MiniPlayerTop = Top;
        s.MiniPlayerWidth = Width;
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

    // ---- Timeline ----

    private void Timeline_Down(object sender, MouseButtonEventArgs e) => _vm.Playback.IsUserSeeking = true;

    private void Timeline_Up(object sender, MouseButtonEventArgs e)
    {
        _vm.Playback.Seek(Timeline.Value);
        _vm.Playback.IsUserSeeking = false;
    }

    private void Timeline_DragStarted(object sender, DragStartedEventArgs e) => _vm.Playback.IsUserSeeking = true;

    private void Timeline_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _vm.Playback.Seek(Timeline.Value);
        _vm.Playback.IsUserSeeking = false;
    }
}
