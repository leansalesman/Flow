using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Flow.Services;

namespace Flow.Views;

/// <summary>
/// Animated equalizer bars shown next to the track that's playing. The bars bounce while music plays
/// and rest when paused; hovering turns the indicator into a pause/play button.
/// </summary>
public sealed class NowPlayingIndicator : Grid
{
    private static readonly double[] Durations = { 0.42, 0.55, 0.36 };
    private static readonly double[] RestLevels = { 0.35, 0.7, 0.5 };

    private readonly ScaleTransform[] _scales = new ScaleTransform[3];
    private readonly StackPanel _bars;
    private readonly TextBlock _button;
    private PlaybackService? _pb;

    public NowPlayingIndicator()
    {
        Width = 18;
        Height = 16;
        Background = Brushes.Transparent; // hit-testable for hover/click
        Cursor = Cursors.Hand;
        ToolTip = "Pause / play";

        _bars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
        for (int i = 0; i < 3; i++)
        {
            var rect = new Rectangle { Width = 3, Height = 14, RadiusX = 1.2, RadiusY = 1.2, Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0) };
            rect.SetResourceReference(Shape.FillProperty, "AccentLightBrush");
            _scales[i] = new ScaleTransform(1, RestLevels[i]);
            rect.RenderTransformOrigin = new Point(0.5, 1);
            rect.RenderTransform = _scales[i];
            _bars.Children.Add(rect);
        }

        _button = new TextBlock
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _button.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _button.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        Children.Add(_bars);
        Children.Add(_button);

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        MouseEnter += (_, _) => ShowButton(true);
        MouseLeave += (_, _) => ShowButton(false);
        MouseLeftButtonDown += (_, e) => e.Handled = true;  // don't select/start dragging the row
        MouseLeftButtonUp += (_, e) =>
        {
            _pb?.PlayPause();
            e.Handled = true;
        };
        IsVisibleChanged += (_, _) => Update();
    }

    private void Attach()
    {
        _pb = PlaybackService.Instance;
        if (_pb != null) _pb.PropertyChanged += OnPlaybackChanged;
        Update();
    }

    private void Detach()
    {
        if (_pb != null) _pb.PropertyChanged -= OnPlaybackChanged;
        StopBars();
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.IsPlaying)) Update();
    }

    private bool Playing => _pb?.IsPlaying == true;

    private void ShowButton(bool show)
    {
        _button.Text = Playing ? "" : "";
        _button.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _bars.Visibility = show ? Visibility.Hidden : Visibility.Visible;
    }

    private void Update()
    {
        if (IsMouseOver) ShowButton(true);
        if (Playing && IsVisible) StartBars();
        else StopBars();
    }

    private void StartBars()
    {
        for (int i = 0; i < 3; i++)
        {
            var anim = new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(Durations[i]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(i * 0.11),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    private void StopBars()
    {
        for (int i = 0; i < 3; i++)
        {
            // Ease back to a still "paused" shape.
            var anim = new DoubleAnimation(RestLevels[i], TimeSpan.FromMilliseconds(250));
            _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }
}
