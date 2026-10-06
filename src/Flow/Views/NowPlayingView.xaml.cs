using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Flow.Library;
using Flow.Services;
using Flow.ViewModels;

using Track = Flow.Library.Track;

namespace Flow.Views;

public partial class NowPlayingView : UserControl
{
    private MainViewModel? _vm;
    private readonly DispatcherTimer _holdTimer;
    private Button? _holdButton;
    private bool _scrubbing, _suppressClick;

    public NowPlayingView()
    {
        InitializeComponent();
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _holdTimer.Tick += HoldTick;
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_vm != null) _vm.Playback.PropertyChanged -= OnPlaybackChanged;
        _vm = DataContext as MainViewModel;
        if (_vm == null) return;
        Viz.Analyzer = _vm.Playback.Engine.Analyzer;
        _vm.Playback.PropertyChanged += OnPlaybackChanged;
        DragReorder.Enable(QueueList, () => true, (from, to) => _vm.Playback.Move(from, to));
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.CurrentArt))
        {
            // Soft cross-fade when the artwork changes.
            ArtImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else if (e.PropertyName == nameof(PlaybackService.CurrentIndex) && _vm != null)
        {
            int i = _vm.Playback.CurrentIndex;
            if (i >= 0 && i < QueueList.Items.Count) QueueList.ScrollIntoView(QueueList.Items[i]);
        }
    }

    /// <summary>When the window chrome fades during playback, the secondary controls dim too.</summary>
    public void SetChromeHidden(bool hidden)
    {
        var anim = new DoubleAnimation(hidden ? 0.35 : 1, TimeSpan.FromMilliseconds(hidden ? 700 : 180));
        SecondaryRow.BeginAnimation(OpacityProperty, anim);
        // The style button disappears completely with the window chrome.
        StyleButton.BeginAnimation(OpacityProperty, new DoubleAnimation(hidden ? 0 : 1, TimeSpan.FromMilliseconds(hidden ? 700 : 180)));
    }

    // ---- Visualizer style switcher ----

    private void StyleButton_Click(object sender, RoutedEventArgs e) => CycleStyle(+1);

    private void StyleButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        CycleStyle(-1);
        e.Handled = true;
    }

    private void CycleStyle(int dir)
    {
        if (_vm == null) return;
        _vm.CycleVisualizer(dir);
        StyleNameText.Text = Flow.Visualizer.MusicVisualizer.DisplayName(_vm.VisualizerStyle);
        var show = new DoubleAnimationUsingKeyFrames();
        show.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        show.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400))));
        show.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1900))));
        StyleNameBadge.BeginAnimation(OpacityProperty, show);
    }

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = Stage.ActualWidth, h = Stage.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double art = Math.Clamp(Math.Min(h * 0.82, Math.Min(w * 0.36, 440)), 120, 440);
        ArtHost.Width = art;
        ArtHost.Height = art;
        Viz.ArtSize = art;
        ControlsPanel.Width = Math.Clamp(art * 1.55, 360, Math.Max(360, w - 48));
    }

    private void Art_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm == null) return;
        _vm.Playback.Volume += e.Delta > 0 ? 0.05 : -0.05;
        _vm.ShowToast($"Volume {Math.Round(_vm.Playback.Volume * 100)}%");
        e.Handled = true;
    }

    // ---- Timeline seeking ----

    private void Timeline_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm != null) _vm.Playback.IsUserSeeking = true;
    }

    private void Timeline_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        _vm.Playback.Seek(Timeline.Value);
        _vm.Playback.IsUserSeeking = false;
    }

    private void Timeline_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (_vm != null) _vm.Playback.IsUserSeeking = true;
    }

    private void Timeline_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_vm == null) return;
        _vm.Playback.Seek(Timeline.Value);
        _vm.Playback.IsUserSeeking = false;
    }

    // ---- Previous / Next: tap to skip, hold to scrub ----

    private void Scrub_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _holdButton = sender as Button;
        _scrubbing = false;
        _holdTimer.Interval = TimeSpan.FromMilliseconds(420);
        _holdTimer.Start();
    }

    private void HoldTick(object? sender, EventArgs e)
    {
        if (_vm == null || _holdButton == null || Mouse.LeftButton != MouseButtonState.Pressed) { StopHold(); return; }
        _scrubbing = true;
        _holdTimer.Interval = TimeSpan.FromMilliseconds(160);
        _vm.Playback.SeekRelative(ReferenceEquals(_holdButton, NextButton) ? 5 : -5);
    }

    private void Scrub_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_scrubbing) _suppressClick = true;
        StopHold();
    }

    private void Scrub_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_scrubbing) _suppressClick = true;
        StopHold();
    }

    private void StopHold()
    {
        _holdTimer.Stop();
        _scrubbing = false;
        _holdButton = null;
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressClick) { _suppressClick = false; return; }
        _vm?.Playback.Previous();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressClick) { _suppressClick = false; return; }
        _vm?.Playback.Next();
    }

    // ---- Queue ----

    private void QueueList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var c = DragReorder.FindContainer(e.OriginalSource as DependencyObject);
        if (c == null || _vm == null) return;
        _vm.Playback.PlayAt(QueueList.ItemContainerGenerator.IndexFromContainer(c));
    }

    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        var c = DragReorder.FindContainer(sender as DependencyObject);
        if (c == null || _vm == null) return;
        _vm.Playback.RemoveAt(QueueList.ItemContainerGenerator.IndexFromContainer(c));
    }

    private void QueueList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_vm == null || QueueList.ContextMenu == null) return;
        var c = DragReorder.FindContainer(e.OriginalSource as DependencyObject);
        if (c == null) { e.Handled = true; return; }
        int index = QueueList.ItemContainerGenerator.IndexFromContainer(c);
        if (c.DataContext is not Track t) { e.Handled = true; return; }
        TrackMenu.Populate(QueueList.ContextMenu, _vm, new[] { t },
            TrackMenu.Item("Remove from queue", "îœ‘", () => _vm.Playback.RemoveAt(index)));
    }
}
