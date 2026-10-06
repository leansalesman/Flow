using System.ComponentModel;
using System.Windows.Threading;
using Flow.Services;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Flow.Interop;

/// <summary>
/// System Media Transport Controls: media keys, the Windows 11 media flyout and lock-screen controls.
/// </summary>
public sealed class SmtcService : IDisposable
{
    private readonly SystemMediaTransportControls? _smtc;
    private readonly PlaybackService _pb;
    private readonly Dispatcher _ui;
    private DateTime _lastTimeline;

    public SmtcService(IntPtr hwnd, PlaybackService pb, Dispatcher ui)
    {
        _pb = pb;
        _ui = ui;
        try
        {
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        }
        catch
        {
            _smtc = null;
            return;
        }

        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.IsStopEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButton;
        _smtc.PlaybackPositionChangeRequested += (_, e) =>
            _ui.BeginInvoke(() => _pb.Seek(e.RequestedPlaybackPosition.TotalSeconds));

        _pb.PropertyChanged += OnPlaybackChanged;
    }

    private void OnButton(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
    {
        _ui.BeginInvoke(() =>
        {
            switch (e.Button)
            {
                case SystemMediaTransportControlsButton.Play: _pb.Resume(); break;
                case SystemMediaTransportControlsButton.Pause: _pb.Pause(); break;
                case SystemMediaTransportControlsButton.Stop: _pb.Pause(); _pb.Seek(0); break;
                case SystemMediaTransportControlsButton.Next: _pb.Next(); break;
                case SystemMediaTransportControlsButton.Previous: _pb.Previous(); break;
            }
        });
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_smtc == null) return;
        switch (e.PropertyName)
        {
            case nameof(PlaybackService.CurrentTrack):
            case nameof(PlaybackService.CurrentArtPath):
                UpdateMetadata();
                UpdateTimeline(true);
                break;
            case nameof(PlaybackService.IsPlaying):
                _smtc.PlaybackStatus = _pb.CurrentTrack == null ? MediaPlaybackStatus.Closed
                    : _pb.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                UpdateTimeline(true);
                break;
            case nameof(PlaybackService.PositionSeconds):
                UpdateTimeline(false);
                break;
        }
    }

    private async void UpdateMetadata()
    {
        if (_smtc == null) return;
        var t = _pb.CurrentTrack;
        var du = _smtc.DisplayUpdater;
        try
        {
            du.ClearAll();
            if (t == null)
            {
                du.Update();
                _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                return;
            }
            du.Type = MediaPlaybackType.Music;
            du.MusicProperties.Title = t.Title;
            du.MusicProperties.Artist = t.DisplayArtist;
            du.MusicProperties.AlbumTitle = t.Album;
            du.MusicProperties.AlbumArtist = t.DisplayAlbumArtist;
            var art = _pb.CurrentArtPath;
            if (art != null)
            {
                var file = await StorageFile.GetFileFromPathAsync(art);
                if (!ReferenceEquals(t, _pb.CurrentTrack)) return;
                du.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            }
            du.Update();
        }
        catch { /* best effort */ }
    }

    private void UpdateTimeline(bool force)
    {
        if (_smtc == null || _pb.CurrentTrack == null) return;
        var now = DateTime.UtcNow;
        if (!force && (now - _lastTimeline).TotalSeconds < 5) return;
        _lastTimeline = now;
        try
        {
            var end = TimeSpan.FromSeconds(Math.Max(0, _pb.DurationSeconds));
            _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                EndTime = end,
                MinSeekTime = TimeSpan.Zero,
                MaxSeekTime = end,
                Position = TimeSpan.FromSeconds(Math.Clamp(_pb.PositionSeconds, 0, end.TotalSeconds)),
            });
        }
        catch { }
    }

    public void Dispose()
    {
        _pb.PropertyChanged -= OnPlaybackChanged;
        if (_smtc != null)
        {
            _smtc.ButtonPressed -= OnButton;
            try { _smtc.IsEnabled = false; } catch { }
        }
    }
}
