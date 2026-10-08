using System.Windows.Threading;
using Flow.Infrastructure;
using Windows.Media.Control;

namespace Flow.Spotify;

/// <summary>
/// Follows this PC's Spotify app through Windows' media sessions (the same source as the volume flyout).
/// No network: it raises <see cref="Changed"/> (on the UI thread) when Spotify's song, play state or position
/// changes, so Flow can check the Web API only then instead of polling every second.
/// </summary>
public sealed class SpotifyAppWatcher
{
    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _starting;

    public SpotifyAppWatcher(Dispatcher ui) => _ui = ui;

    /// <summary>Raised on the UI thread when the Spotify app's song, play state or timeline changes.</summary>
    public event Action? Changed;

    /// <summary>The Spotify app on this PC has a media session that Flow is following.</summary>
    public bool Present => _session != null;

    public async Task StartAsync()
    {
        if (_manager != null || _starting) return;
        _starting = true;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => _ui.BeginInvoke(Attach);
            Attach();
        }
        catch (Exception ex) { DiagLog.Write("Spotify media session unavailable: " + ex.Message); }
        finally { _starting = false; }
    }

    private void Attach()
    {
        if (_manager == null) return;
        GlobalSystemMediaTransportControlsSession? found = null;
        try
        {
            found = _manager.GetSessions().FirstOrDefault(s =>
                s.SourceAppUserModelId?.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch { }
        if (found == null && _session == null) return;
        if (found != null && _session != null && found.SourceAppUserModelId == _session.SourceAppUserModelId) return;

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnChanged;
            _session.PlaybackInfoChanged -= OnChanged;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }
        _session = found;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnChanged;
            _session.PlaybackInfoChanged += OnChanged;
            _session.TimelinePropertiesChanged += OnTimeline;
            _lastPos = null;
        }
        SpotifyLog.Write(_session != null ? "Following the Spotify app's media session" : "Spotify app's media session closed");
        _ui.BeginInvoke(() => Changed?.Invoke());
    }

    private void OnChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _ui.BeginInvoke(() => Changed?.Invoke());

    private TimeSpan? _lastPos;
    private DateTime _lastPosAt;

    // Spotify updates its timeline while it plays; only a jump (a seek in the app) is worth a check.
    private void OnTimeline(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        try
        {
            var pos = sender.GetTimelineProperties().Position;
            var now = DateTime.UtcNow;
            bool playing = sender.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            bool jumped = _lastPos is { } last &&
                          Math.Abs((pos - (playing ? last + (now - _lastPosAt) : last)).TotalSeconds) > 2.5;
            _lastPos = pos;
            _lastPosAt = now;
            if (jumped) _ui.BeginInvoke(() => Changed?.Invoke());
        }
        catch { }
    }
}
