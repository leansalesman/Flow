using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using Flow.Infrastructure;
using Flow.Services;

namespace Flow.Spotify;

/// <summary>A Spotify Connect device (this PC's Spotify app, a phone, a speaker, …).</summary>
public sealed record SpotifyDevice(string Id, string Name, string Type, bool IsActive);

/// <summary>
/// Plays Spotify tracks through the user's Spotify app / Spotify Connect device and follows its state.
/// Flow hands Spotify a "run" of consecutive queue items; this class reports when Spotify moves to another
/// item of the run, when the run has finished, or when Spotify starts playing something else.
/// All members are used on the UI thread.
/// </summary>
public sealed class SpotifyPlayback
{
    private readonly SpotifyService _api;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _volumeDebounce;
    private List<string> _run = new();
    private string? _lastUri;
    private long _progressMs, _durationMs;
    private DateTime _progressAt;
    private bool _playing, _polling;
    private DateTime _lastCommand;
    private int _pendingVolume = -1;

    // Each play request gets a new generation; anything still in flight from an older one is ignored.
    private int _generation;
    // A different song must be seen on two polls in a row before Flow acts on it (Spotify's state API
    // occasionally returns a stale snapshot for one request).
    private string? _pendingForeign;
    private int _pendingForeignCount, _pendingGoneCount;

    public event Action<string>? ItemChanged;
    public event Action? RunEnded;
    public event Action? ForeignItem;
    public event Action<bool>? PlayingChanged;
    public event Action<string>? Info;

    private readonly SettingsService? _settings;
    private readonly LibrespotHost? _librespot;
    private long _anchorMs, _anchorFrames;   // built-in engine: position = anchor + frames played since

    public SpotifyPlayback(SpotifyService api, Dispatcher ui, SettingsService? settings = null, LibrespotHost? librespot = null)
    {
        _api = api;
        _settings = settings;
        _librespot = librespot;
        _poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(1000) };
        _poll.Tick += async (_, _) => await PollAsync();
        _volumeDebounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(350) };
        _volumeDebounce.Tick += async (_, _) =>
        {
            _volumeDebounce.Stop();
            // With the built-in engine Flow's own volume is the master; librespot stays at full scale.
            if (_pendingVolume >= 0 && Active && !OnBuiltInDevice) await _api.SendAsync(HttpMethod.Put, $"/me/player/volume?volume_percent={_pendingVolume}");
        };
    }

    public bool Active { get; private set; }

    /// <summary>Settings → Spotify → Playback engine is "Built-in (librespot)" and librespot is available.</summary>
    public bool BuiltIn => _librespot != null && _settings?.Current.SpotifyEngine == SpotifyEngine.BuiltIn && LibrespotHost.IsAvailable;

    /// <summary>The song plays on Flow's own librespot device (audio comes through Flow's engine).</summary>
    public bool OnBuiltInDevice { get; private set; }

    private string BuiltInName => string.IsNullOrWhiteSpace(_settings?.Current.LibrespotDeviceName) ? "Flow" : _settings!.Current.LibrespotDeviceName.Trim();
    /// <summary>The device picked with "Play on…"; null = whichever device Spotify has active (the default).</summary>
    public string? ChosenDeviceId { get; private set; }
    public bool IsPlaying => _playing;
    public string? DeviceName { get; private set; }
    private bool IsLocalDevice => string.Equals(DeviceName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) || _deviceIsComputer;
    private bool _deviceIsComputer;

    public double PositionSeconds
    {
        get
        {
            if (OnBuiltInDevice && _librespot != null)
            {
                // Exact: what Flow has actually played since the last play / seek / track change.
                long played = (_librespot.Input.FramesConsumed - _anchorFrames) * 1000 / LibrespotHost.SampleRate;
                long pos = Math.Max(0, _anchorMs + played);
                return (_durationMs > 0 ? Math.Min(pos, _durationMs) : pos) / 1000.0;
            }
            long ms = _progressMs;
            if (_playing) ms += (long)(DateTime.UtcNow - _progressAt).TotalMilliseconds;
            if (_durationMs > 0) ms = Math.Min(ms, _durationMs);
            return ms / 1000.0;
        }
    }

    public double DurationSeconds => _durationMs / 1000.0;

    /// <summary>
    /// Starts playing the given track URIs (first one first) at an optional position. When the album is known,
    /// playback is started as "album, starting at this track": some Spotify desktop clients silently ignore
    /// bare track URIs sent through the Web API but honour album contexts.
    /// </summary>
    public async Task<string?> PlayAsync(IReadOnlyList<string> uris, string? albumUri, double startSeconds, long firstDurationMs)
    {
        SpotifyLog.Write($"Play requested: {uris.Count} track(s), first {uris[0]} in {albumUri ?? "(no album)"} at {startSeconds:0.0}s");
        long startMs = (long)(startSeconds * 1000);

        // Stop following the previous song immediately so its tracker can't react to this switch,
        // and show the new song's position (not the old one's) while we wait for Spotify.
        int gen = ++_generation;
        string? previousUri = _lastUri;
        Active = false;
        _poll.Stop();
        _pendingForeign = null;
        _pendingForeignCount = _pendingGoneCount = 0;
        _progressMs = startMs;
        _durationMs = firstDurationMs;
        _progressAt = DateTime.UtcNow;

        var dev = await EnsureDeviceAsync();
        if (gen != _generation) return Superseded();
        if (dev == null)
        {
            SpotifyLog.Write("No device available");
            return BuiltIn
                ? "Flow's built-in Spotify player isn't available yet. Check Settings, Spotify, Playback engine."
                : "No Spotify device found. Open Spotify on this PC (or any device) and try again.";
        }
        var (device, _) = dev.Value;
        // Slower polling when the audio comes through Flow (position is exact; the poll only follows track changes).
        _poll.Interval = TimeSpan.FromMilliseconds(OnBuiltInDevice ? 3000 : 1000);
        if (OnBuiltInDevice) Anchor(startMs, flush: true);

        _deviceId = device;
        _albumUri = albumUri;
        _pendingSeekMs = -1;
        _seekTargetMs = -1;
        object body = albumUri != null
            ? new { context_uri = albumUri, offset = new { uri = uris[0] }, position_ms = startMs }
            : new { uris = uris.ToArray(), position_ms = startMs };
        SpotifyService.ApiResult r = new(HttpStatusCode.ServiceUnavailable, null);
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            r = await _api.SendAsync(HttpMethod.Put, $"/me/player/play?device_id={device}", body);
            if (gen != _generation) return Superseded();
            SpotifyLog.Write($"Play attempt {attempt}: {(int)r.Status} {Short(r)}");
            if (r.Ok || r.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) break;
            await Task.Delay(1500); // device still waking up (404/502/503)
        }
        if (!r.Ok && r.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return r.Status == HttpStatusCode.Forbidden
                ? "Spotify refused playback — check that this account has Spotify Premium."
                : "Spotify session expired — reconnect in Settings.";
        }

        // Verify the song really started.
        string? playingUri = r.Ok ? await WaitForStartAsync(uris[0], previousUri, startMs, gen) : null;
        if (gen != _generation) return Superseded();
        if (playingUri == null && IsLocalDevice && !BuiltIn)
        {
            // Last resort for this PC: ask the Spotify app to open the track itself (bypasses the cloud command).
            SpotifyLog.Write("Remote command didn't start playback - opening the track in the local Spotify app");
            try { Process.Start(new ProcessStartInfo(uris[0]) { UseShellExecute = true }); } catch { }
            playingUri = await WaitForStartAsync(uris[0], previousUri, 0, gen, attempts: 10);
            if (gen != _generation) return Superseded();
            if (playingUri != null && startMs > 1500)
                await _api.SendAsync(HttpMethod.Put, $"/me/player/seek?position_ms={startMs}");
        }
        bool started = playingUri != null;
        SpotifyLog.Write(started ? $"Playback confirmed ({playingUri})" : "Playback could not be confirmed");
        if (!started)
            return "Spotify didn't start the song. Open the Spotify app, play any song once, then try again in Flow.";
        // Flow owns shuffle/repeat; make sure Spotify's own modes don't reorder the run.
        _ = _api.SendAsync(HttpMethod.Put, $"/me/player/shuffle?state=false&device_id={device}");
        _ = _api.SendAsync(HttpMethod.Put, $"/me/player/repeat?state=off&device_id={device}");

        _run = uris.ToList();
        if (playingUri != uris[0]) _run[0] = playingUri!; // Spotify substituted a regional copy of the track
        _lastUri = _run[0];
        _progressMs = startMs;
        _durationMs = firstDurationMs;
        _progressAt = DateTime.UtcNow;
        _lastCommand = DateTime.UtcNow;
        Active = true;          // before SetPlaying so listeners see an active player
        SetPlaying(true);
        _poll.Start();
        return null;
    }

    private string? Superseded()
    {
        SpotifyLog.Write("Superseded by a newer play request");
        return null; // the newer request owns the player now; the caller's own token check discards this one
    }

    /// <summary>Returns the URI Spotify is actually playing once our song has started, or null.</summary>
    private async Task<string?> WaitForStartAsync(string uri, string? previousUri, long startMs, int gen, int attempts = 6)
    {
        for (int i = 0; i < attempts; i++)
        {
            await Task.Delay(500);
            if (gen != _generation) return null;
            var s = await _api.GetAsync("/me/player");
            var playing = s.Json?["is_playing"]?.GetValue<bool>() ?? false;
            var current = s.Json?["item"]?["uri"]?.GetValue<string>();
            long progress = s.Json?["progress_ms"]?.GetValue<long>() ?? long.MaxValue;
            if (playing && current == uri) return current;
            // Track relinking: the same song under another ID, just started where we asked.
            // Never accept the song that was playing before this request.
            if (playing && current != null && current != previousUri &&
                progress < startMs + 4000 && progress >= Math.Max(0, startMs - 1000)) return current;
            if (i == attempts - 1) SpotifyLog.Write($"State check: {(int)s.Status} playing={playing} item={current ?? "none"}");
        }
        return null;
    }

    private static string Short(SpotifyService.ApiResult r)
    {
        var msg = r.Json?["error"]?["message"]?.GetValue<string>() ?? r.Json?["error"]?["reason"]?.GetValue<string>();
        return msg ?? "";
    }

    public async Task PauseAsync()
    {
        if (!Active) return;
        _progressMs = (long)(PositionSeconds * 1000);
        _progressAt = DateTime.UtcNow;
        SetPlaying(false);
        _lastCommand = DateTime.UtcNow;
        await _api.SendAsync(HttpMethod.Put, "/me/player/pause");
    }

    public async Task<bool> ResumeAsync()
    {
        if (!Active) return false;
        _progressAt = DateTime.UtcNow;
        SetPlaying(true);
        _lastCommand = DateTime.UtcNow;
        if (_pendingSeekMs >= 0 && OnBuiltInDevice)
        {
            // Seeked while paused (built-in engine): start again from the chosen spot.
            long ms = _pendingSeekMs;
            _pendingSeekMs = -1;
            return await ReplayAtAsync(ms);
        }
        var r = await _api.SendAsync(HttpMethod.Put, "/me/player/play");
        return r.Ok;
    }

    public async Task SeekAsync(double seconds)
    {
        if (!Active) return;
        _progressMs = (long)(seconds * 1000);
        _progressAt = DateTime.UtcNow;
        _lastCommand = DateTime.UtcNow;
        if (OnBuiltInDevice)
        {
            // librespot v0.8 drops the Web API's seek command, so the built-in engine seeks by playing the
            // current song again from the new position (the same command that starts songs, which it honours).
            // While the same song is playing, Spotify turns that play command into a seek_to (which librespot
            // drops); while paused it delivers a real load. So pause first, then play from the new spot.
            Anchor(_progressMs, flush: true);
            if (!_playing) { _pendingSeekMs = _progressMs; return; }   // applied on resume

            // One seek at a time: a click on the timeline fires two seeks at once (mouse up + drag end), and
            // overlapping pause/play pairs make Spotify answer 503 to both. A seek that arrives while one is
            // running just moves the target; the running loop plays the newest one.
            _seekTargetMs = _progressMs;
            if (_seeking) return;
            _seeking = true;
            try
            {
                while (_seekTargetMs >= 0)
                {
                    long ms = _seekTargetMs;
                    _seekTargetMs = -1;
                    await _api.SendAsync(HttpMethod.Put, $"/me/player/pause?device_id={_deviceId}");
                    if (!await ReplayAtAsync(ms))
                    {
                        await Task.Delay(700);   // device busy (503): try once more
                        await ReplayAtAsync(ms);
                    }
                }
            }
            finally { _seeking = false; }
            return;
        }
        await _api.SendAsync(HttpMethod.Put, $"/me/player/seek?position_ms={_progressMs}");
    }

    private string? _deviceId, _albumUri;
    private long _pendingSeekMs = -1;
    private long _seekTargetMs = -1;
    private bool _seeking;

    /// <summary>Built-in engine: restarts the current song (in its album run) at the given position.</summary>
    private async Task<bool> ReplayAtAsync(long ms)
    {
        if (_lastUri == null || _deviceId == null) return false;
        object body = _albumUri != null
            ? new { context_uri = _albumUri, offset = new { uri = _lastUri }, position_ms = ms }
            : new { uris = _run.SkipWhile(u => u != _lastUri).ToArray(), position_ms = ms };
        Anchor(ms, flush: true);
        _lastCommand = DateTime.UtcNow;
        var r = await _api.SendAsync(HttpMethod.Put, $"/me/player/play?device_id={_deviceId}", body);
        Anchor(ms, flush: true);   // drop what the old position sent while the command was on its way
        _lastCommand = DateTime.UtcNow;
        if (!r.Ok) SpotifyLog.Write($"Seek (replay at {ms / 1000.0:0.0}s): {(int)r.Status} {Short(r)}");
        return r.Ok;
    }

    public void SetVolume(double volume01)
    {
        _pendingVolume = (int)Math.Round(Math.Clamp(volume01, 0, 1) * 100);
        _volumeDebounce.Stop();
        _volumeDebounce.Start();
    }

    /// <summary>Stops following Spotify (optionally pausing it), e.g. when Flow switches to a local file.</summary>
    public async Task DeactivateAsync(bool pause)
    {
        if (!Active) return;
        Active = false;
        _poll.Stop();
        bool wasPlaying = _playing;
        SetPlaying(false);
        if (pause && wasPlaying) await _api.SendAsync(HttpMethod.Put, "/me/player/pause");
    }

    /// <summary>Pauses the Spotify player regardless of whether Flow is following it (e.g. stops Spotify autoplay).</summary>
    public Task PauseRemoteAsync() => _api.SendAsync(HttpMethod.Put, "/me/player/pause");

    /// <summary>Best-effort synchronous pause used while Flow is shutting down.</summary>
    public void PauseOnExit()
    {
        try { Task.Run(() => _api.SendAsync(HttpMethod.Put, "/me/player/pause")).Wait(2000); } catch { }
    }

    private void SetPlaying(bool p)
    {
        if (_playing == p) return;
        _playing = p;
        PlayingChanged?.Invoke(p);
    }

    private async Task<(string Id, bool Active)?> EnsureDeviceAsync()
    {
        if (BuiltIn)
        {
            // Built-in engine: start librespot and wait for it to appear in Spotify Connect. Never launch the app.
            if (!await _librespot!.EnsureRunningAsync()) return null;
            for (int i = 0; i < 15; i++)
            {
                var d = await PickDeviceAsync();
                if (d != null) return d;
                await Task.Delay(1000);
            }
            SpotifyLog.Write($"Built-in device \"{BuiltInName}\" did not appear in Spotify Connect");
            return null;
        }
        var device = await PickDeviceAsync();
        if (device != null) return device;

        // Nothing available: start the Spotify desktop app and wait for it to register as a device.
        SpotifyLog.Write("No devices - launching the Spotify app");
        Info?.Invoke("Opening Spotify…");
        try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
        catch (Exception ex) { SpotifyLog.Write("Could not launch Spotify: " + ex.Message); return null; }
        for (int i = 0; i < 25; i++)
        {
            await Task.Delay(1000);
            device = await PickDeviceAsync();
            if (device != null)
            {
                await Task.Delay(2500); // registered, but give the app a moment to finish starting
                return device;
            }
        }
        return null;
    }

    /// <summary>The Spotify Connect devices currently available to this account.</summary>
    public async Task<IReadOnlyList<SpotifyDevice>> GetDevicesAsync()
    {
        var r = await _api.GetAsync("/me/player/devices");
        if (!r.Ok || r.Json?["devices"] is not System.Text.Json.Nodes.JsonArray devices) return Array.Empty<SpotifyDevice>();
        return devices
            .Where(d => d != null && d["is_restricted"]?.GetValue<bool>() != true && d["id"] != null)
            .Select(d => new SpotifyDevice(d!["id"]!.GetValue<string>(), d["name"]?.GetValue<string>() ?? "Unknown device",
                                           d["type"]?.GetValue<string>() ?? "", d["is_active"]?.GetValue<bool>() == true))
            .ToList();
    }

    /// <summary>
    /// "Play on…": Spotify songs go to this device from now on. A Spotify song that is playing right now moves
    /// there and carries on from the same spot. Returns an error message, or null.
    /// </summary>
    public async Task<string?> PlayOnDeviceAsync(SpotifyDevice device)
    {
        ChosenDeviceId = device.Id;
        SpotifyLog.Write($"User chose device: {device.Name} ({device.Type})");
        if (!Active) return null;
        _lastCommand = DateTime.UtcNow;
        var r = await _api.SendAsync(HttpMethod.Put, "/me/player", new { device_ids = new[] { device.Id }, play = _playing });
        SpotifyLog.Write($"Transfer to {device.Name}: {(int)r.Status} {Short(r)}");
        if (!r.Ok) return $"Spotify couldn't move playback to {device.Name}.";
        DeviceName = device.Name;
        _deviceId = device.Id;
        _deviceIsComputer = device.Type == "Computer";
        bool wasBuiltIn = OnBuiltInDevice;
        OnBuiltInDevice = BuiltIn && string.Equals(device.Name, BuiltInName, StringComparison.OrdinalIgnoreCase);
        if (OnBuiltInDevice && !wasBuiltIn) Anchor((long)(PositionSeconds * 1000), flush: true);
        _lastCommand = DateTime.UtcNow;
        return null;
    }

    /// <summary>Back to the default: play on whichever device Spotify has active.</summary>
    public void UseActiveDevice()
    {
        ChosenDeviceId = null;
        SpotifyLog.Write("User chose: Spotify's active device");
    }

    private async Task<(string Id, bool Active)?> PickDeviceAsync()
    {
        var r = await _api.GetAsync("/me/player/devices");
        if (!r.Ok || r.Json?["devices"] is not System.Text.Json.Nodes.JsonArray devices || devices.Count == 0)
        {
            SpotifyLog.Write($"Devices: {(int)r.Status}, none listed");
            return null;
        }
        var list = devices.Where(d => d != null && d["is_restricted"]?.GetValue<bool>() != true && d["id"] != null).ToList();
        SpotifyLog.Write("Devices: " + string.Join(", ", list.Select(d =>
            $"{d!["name"]?.GetValue<string>()} ({d["type"]?.GetValue<string>()}{(d["is_active"]?.GetValue<bool>() == true ? ", active" : "")})")));
        // A device picked with "Play on…" wins while it's available; otherwise the built-in "Flow" device
        // (built-in engine) or Spotify's active device.
        var chosen = ChosenDeviceId == null ? null : list.FirstOrDefault(d => d!["id"]?.GetValue<string>() == ChosenDeviceId);
        if (BuiltIn)
        {
            var own = list.FirstOrDefault(d => string.Equals(d!["name"]?.GetValue<string>(), BuiltInName, StringComparison.OrdinalIgnoreCase));
            var builtInPick = chosen ?? own;
            if (builtInPick == null) return null;
            DeviceName = builtInPick["name"]?.GetValue<string>();
            _deviceIsComputer = false;  // never fall back to opening the Spotify app
            OnBuiltInDevice = ReferenceEquals(builtInPick, own);
            return (builtInPick["id"]!.GetValue<string>(), builtInPick["is_active"]?.GetValue<bool>() == true);
        }
        OnBuiltInDevice = false;
        var pick = chosen
                   ?? list.FirstOrDefault(d => d!["is_active"]?.GetValue<bool>() == true)
                   ?? list.FirstOrDefault(d => string.Equals(d!["name"]?.GetValue<string>(), Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(d => d!["type"]?.GetValue<string>() == "Computer")
                   ?? list.FirstOrDefault();
        if (pick == null) return null;
        DeviceName = pick["name"]?.GetValue<string>();
        _deviceIsComputer = pick["type"]?.GetValue<string>() == "Computer";
        return (pick["id"]!.GetValue<string>(), pick["is_active"]?.GetValue<bool>() == true);
    }

    private async Task PollAsync()
    {
        if (!Active || _polling) return;
        _polling = true;
        int gen = _generation;
        try
        {
            var r = await _api.GetAsync("/me/player");
            // A newer play request (or a stop) happened while this request was in flight: drop the result.
            if (!Active || gen != _generation) return;

            bool settled = (DateTime.UtcNow - _lastCommand).TotalMilliseconds > 1500;
            bool wasNearEnd = _durationMs > 0 && PositionSeconds * 1000 >= _durationMs - 4000;
            bool lastOfRun = _lastUri != null && _run.Count > 0 && _lastUri == _run[^1];

            if (r.Status == HttpStatusCode.NoContent || r.Json == null || !r.Ok)
            {
                // Nothing playing anywhere any more (confirm on two polls before acting).
                if (!settled) return;
                if (_playing && lastOfRun && wasNearEnd)
                {
                    if (++_pendingGoneCount >= 2) { SpotifyLog.Write("Run finished (player idle)"); Finish(); }
                    return;
                }
                if (r.Status == HttpStatusCode.NoContent && ++_pendingGoneCount >= 2) SetPlaying(false);
                return;
            }
            _pendingGoneCount = 0;

            var item = r.Json["item"];
            var uri = item?["uri"]?.GetValue<string>();
            bool playing = r.Json["is_playing"]?.GetValue<bool>() ?? false;
            long progress = r.Json["progress_ms"]?.GetValue<long>() ?? 0;
            long duration = item?["duration_ms"]?.GetValue<long>() ?? _durationMs;
            DeviceName = r.Json["device"]?["name"]?.GetValue<string>() ?? DeviceName;

            if (uri != null && uri != _lastUri)
            {
                if (_run.Contains(uri))
                {
                    // Spotify moved on to the next song of the album run - that's expected, follow it.
                    _pendingForeign = null;
                    _pendingForeignCount = 0;
                    _lastUri = uri;
                    Update(progress, duration, playing);
                    if (OnBuiltInDevice)
                    {
                        // librespot reports where it is decoding; what's audible lags by Flow's buffer.
                        long lag = (long)(_librespot!.Input.BufferedSeconds * 1000);
                        Anchor(Math.Max(0, progress - lag), flush: false);
                    }
                    ItemChanged?.Invoke(uri);
                    return;
                }
                if (!settled) return; // stale snapshot from before our command

                // Something else is playing. Make sure it isn't a one-off stale reply.
                if (uri == _pendingForeign) _pendingForeignCount++;
                else { _pendingForeign = uri; _pendingForeignCount = 1; }
                if (_pendingForeignCount < 2) return;

                _pendingForeign = null;
                _pendingForeignCount = 0;
                if (lastOfRun && wasNearEnd)
                {
                    SpotifyLog.Write($"Run finished; Spotify continued with {uri}");
                    Finish();                                   // our song ended, Spotify autoplay took over
                }
                else
                {
                    SpotifyLog.Write($"Spotify switched to {uri} outside Flow - stepping aside");
                    Active = false;
                    _poll.Stop();
                    SetPlaying(false);
                    ForeignItem?.Invoke();
                }
                return;
            }
            _pendingForeign = null;
            _pendingForeignCount = 0;

            if (!settled)
            {
                // Right after a command Spotify sometimes reports an old position; only accept close readings.
                if (Math.Abs(progress - PositionSeconds * 1000) < 2500) Update(progress, duration, _playing);
                return;
            }
            if (!playing && _playing && lastOfRun && (progress == 0 || progress >= duration - 2500) && wasNearEnd)
            {
                SpotifyLog.Write("Run finished (stopped at end)");
                Finish();
                return;
            }
            Update(progress, duration, playing);
        }
        catch (Exception ex) { DiagLog.Write("Spotify poll failed: " + ex.Message); }
        finally { _polling = false; }
    }

    private void Anchor(long ms, bool flush)
    {
        if (_librespot == null) return;
        if (flush) _librespot.Input.Flush();
        _anchorMs = ms;
        _anchorFrames = _librespot.Input.FramesConsumed;
    }

    private void Update(long progress, long duration, bool playing)
    {
        _progressMs = progress;
        _durationMs = duration;
        _progressAt = DateTime.UtcNow;
        SetPlaying(playing);
    }

    private void Finish()
    {
        Active = false;
        _poll.Stop();
        SetPlaying(false);
        RunEnded?.Invoke();
    }
}
