using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Flow.Audio;
using Flow.Infrastructure;
using Flow.Library;
using Flow.Spotify;

namespace Flow.Services;

/// <summary>
/// Queue, transport, shuffle/repeat, play statistics and resume state. Lives on the UI thread.
/// </summary>
public sealed class PlaybackService : ObservableObject, IDisposable
{
    private readonly AudioEngine _engine;
    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();

    private List<Track> _originalOrder = new();
    private int _loadToken, _preloadToken;
    private int _nextIndex = -1;
    private Track? _nextTrack;
    private bool _countedThisPlay;

    private readonly SpotifyPlayback _sp;
    private readonly LoopbackTap _loopback;

    public event Action<string>? Notify;

    public PlaybackService(AudioEngine engine, LibraryService library, SettingsService settings, SpotifyPlayback spotify, Dispatcher ui)
    {
        Instance = this;
        _engine = engine;
        _library = library;
        _settings = settings;
        _ui = ui;
        _sp = spotify;
        _loopback = new LoopbackTap(engine.Analyzer, engine.SampleRate);
        _sp.ItemChanged += OnSpotifyItemChanged;
        _sp.RunEnded += OnSpotifyRunEnded;
        _sp.ForeignItem += () =>
        {
            IsPlaying = false;
            _loopback.Stop();
            Notify?.Invoke("Spotify started playing something else, so Flow paused its queue");
        };
        _sp.Info += msg => Notify?.Invoke(msg);
        _sp.PlayingChanged += p => { if (CurrentIsSpotify && _sp.Active) IsPlaying = p; };

        var s = settings.Current;
        _volume = Math.Clamp(s.Volume, 0, 1);
        _isMuted = s.Muted;
        _shuffle = s.Shuffle;
        _repeat = s.Repeat;
        ApplyVolume();

        _engine.TrackAdvanced += src => _ui.BeginInvoke(() => OnEngineAdvanced(src));
        _engine.PlaybackEnded += () => _ui.BeginInvoke(OnEngineEnded);
        _engine.OutputError += msg => _ui.BeginInvoke(() => Notify?.Invoke(msg));

        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>The app's playback service (used by small view widgets like the now-playing indicator).</summary>
    public static PlaybackService? Instance { get; private set; }

    public AudioEngine Engine => _engine;
    public ObservableCollection<Track> Queue { get; } = new();

    // ---- Observable state ------------------------------------------------------------------

    private int _index = -1;
    public int CurrentIndex { get => _index; private set => Set(ref _index, value); }

    private Track? _current;
    public Track? CurrentTrack
    {
        get => _current;
        private set
        {
            if (ReferenceEquals(_current, value)) return;
            if (_current != null) _current.IsPlayingNow = false;
            _current = value;
            if (_current != null) _current.IsPlayingNow = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTrack));
            OnPropertyChanged(nameof(CurrentIsSpotify));
            OnPropertyChanged(nameof(PlayingOnText));
        }
    }

    public bool HasTrack => _current != null;
    public bool CurrentIsSpotify => _current?.IsSpotify == true;

    public string PlayingOnText => CurrentIsSpotify
        ? "Playing on Spotify" + (_sp.DeviceName != null ? "  ·  " + _sp.DeviceName : "")
        : "";

    private ImageSource? _art;
    public ImageSource? CurrentArt { get => _art; private set => Set(ref _art, value); }

    private string? _artPath;
    public string? CurrentArtPath { get => _artPath; private set => Set(ref _artPath, value); }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }

    private double _position;
    public double PositionSeconds { get => _position; private set => Set(ref _position, value); }

    private double _duration;
    public double DurationSeconds { get => _duration; private set => Set(ref _duration, value); }

    /// <summary>Set by the timeline while the user drags, so position updates don't fight the thumb.</summary>
    public bool IsUserSeeking { get; set; }

    private bool _shuffle;
    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (!Set(ref _shuffle, value)) return;
            _settings.Current.Shuffle = value;
            Reorder();
        }
    }

    private RepeatMode _repeat;
    public RepeatMode Repeat
    {
        get => _repeat;
        set
        {
            if (!Set(ref _repeat, value)) return;
            _settings.Current.Repeat = value;
            PreloadNext();
        }
    }

    private double _volume;
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (!Set(ref _volume, value)) return;
            if (value > 0 && _isMuted) IsMuted = false;
            _settings.Current.Volume = value;
            ApplyVolume();
            if (CurrentIsSpotify && _sp.Active) _sp.SetVolume(_isMuted ? 0 : value);
        }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (!Set(ref _isMuted, value)) return;
            _settings.Current.Muted = value;
            ApplyVolume();
            if (CurrentIsSpotify && _sp.Active) _sp.SetVolume(value ? 0 : _volume);
        }
    }

    private void ApplyVolume() => _engine.Volume = _isMuted ? 0f : (float)_volume;

    // ---- Commands ----------------------------------------------------------------------------

    public void PlayTracks(IReadOnlyList<Track> tracks, int startIndex = 0, bool? shuffle = null)
    {
        if (tracks.Count == 0) return;
        if (shuffle.HasValue && shuffle.Value != _shuffle)
        {
            _shuffle = shuffle.Value;
            _settings.Current.Shuffle = _shuffle;
            OnPropertyChanged(nameof(Shuffle));
        }
        startIndex = Math.Clamp(startIndex, 0, tracks.Count - 1);
        _originalOrder = tracks.ToList();
        var list = tracks.ToList();
        int start = startIndex;
        if (_shuffle)
        {
            Track? first = shuffle == true && startIndex == 0 ? null : list[startIndex];
            list = ShuffledWithFirst(list, first);
            start = 0;
        }
        ReplaceQueue(list);
        _ = LoadAsync(start, play: true, TimeSpan.Zero);
    }

    public void PlayFiles(IEnumerable<string> paths, bool append = false)
    {
        var files = ExpandPaths(paths).ToList();
        if (files.Count == 0) return;
        Task.Run(() => files.Select(f => _library.GetOrRead(f)).Where(t => t != null).Cast<Track>().ToList())
            .ContinueWith(t =>
            {
                var tracks = t.Result;
                if (tracks.Count == 0) return;
                if (append && HasTrack) AddToQueue(tracks);
                else PlayTracks(tracks, 0);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Where(AudioFormats.IsSupported)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch { continue; }
                foreach (var f in files) yield return f;
            }
            else if (File.Exists(p) && AudioFormats.IsSupported(p)) yield return p;
            else if (File.Exists(p) && (p.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)))
                foreach (var f in PlaylistFile.Read(p)) yield return f;
        }
    }

    public void PlayPause()
    {
        if (CurrentTrack == null)
        {
            if (Queue.Count > 0) { _ = LoadAsync(Math.Max(0, _index), true, TimeSpan.Zero); return; }
            var all = _library.Snapshot();
            if (all.Count > 0) PlayTracks(all, 0, shuffle: true);
            return;
        }
        if (IsPlaying) Pause(); else Resume();
    }

    public void Pause()
    {
        DiagLog.Write("Pause");
        if (CurrentIsSpotify) _ = _sp.PauseAsync();
        else _engine.Pause();
        IsPlaying = false;
    }

    public void Resume()
    {
        if (CurrentTrack == null) { PlayPause(); return; }
        if (CurrentIsSpotify)
        {
            ResumeSpotify();
            return;
        }
        if (_engine.Length == TimeSpan.Zero)
        {
            // Source was released (e.g. playback ended) - reload.
            _ = LoadAsync(_index, true, TimeSpan.Zero);
            return;
        }
        _engine.Play();
        IsPlaying = true;
    }

    public void Next()
    {
        if (Queue.Count == 0) return;
        int n = _index + 1;
        if (n >= Queue.Count)
        {
            if (_repeat == RepeatMode.All) n = 0;
            else { _ = LoadAsync(0, false, TimeSpan.Zero); return; }
        }
        _ = LoadAsync(n, true, TimeSpan.Zero);
    }

    public void Previous()
    {
        if (Queue.Count == 0) return;
        if (PositionSeconds > 3) { Seek(0); return; }
        int p = _index - 1;
        if (p < 0) p = _repeat == RepeatMode.All ? Queue.Count - 1 : 0;
        _ = LoadAsync(p, true, TimeSpan.Zero);
    }

    public void Seek(double seconds)
    {
        if (CurrentTrack == null) return;
        seconds = Math.Clamp(seconds, 0, Math.Max(0, DurationSeconds - 0.25));
        if (CurrentIsSpotify)
        {
            if (_sp.Active) _ = _sp.SeekAsync(seconds);
        }
        else _engine.Seek(TimeSpan.FromSeconds(seconds));
        PositionSeconds = seconds;
    }

    public void SeekRelative(double delta) => Seek(PositionSeconds + delta);

    public void CycleRepeat() => Repeat = _repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    public void ToggleFavorite(Track? t = null)
    {
        t ??= CurrentTrack;
        if (t == null) return;
        t.IsFavorite = !t.IsFavorite;
        _library.SaveStats(t);
    }

    public void PlayAt(int index)
    {
        if (index >= 0 && index < Queue.Count) _ = LoadAsync(index, true, TimeSpan.Zero);
    }

    public void PlayNext(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        if (list.Count == 0) return;
        if (!HasTrack) { PlayTracks(list); return; }
        int at = _index + 1;
        foreach (var t in list) Queue.Insert(at++, t);
        _originalOrder.AddRange(list);
        PreloadNext();
        Notify?.Invoke(list.Count == 1 ? $"“{list[0].Title}” will play next" : $"{list.Count} tracks will play next");
    }

    public void AddToQueue(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        if (list.Count == 0) return;
        if (!HasTrack && Queue.Count == 0) { PlayTracks(list); return; }
        foreach (var t in list) Queue.Add(t);
        _originalOrder.AddRange(list);
        PreloadNext();
        Notify?.Invoke(list.Count == 1 ? $"Added “{list[0].Title}” to the queue" : $"Added {list.Count} tracks to the queue");
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= Queue.Count) return;
        var t = Queue[index];
        Queue.RemoveAt(index);
        _originalOrder.Remove(t);
        if (index < _index) CurrentIndex = _index - 1;
        else if (index == _index)
        {
            if (Queue.Count == 0) { StopAndClear(); return; }
            _ = LoadAsync(Math.Min(index, Queue.Count - 1), IsPlaying, TimeSpan.Zero);
            return;
        }
        PreloadNext();
    }

    public void Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Queue.Count || to >= Queue.Count) return;
        Queue.Move(from, to);
        if (_current != null) CurrentIndex = Queue.IndexOf(_current);
        PreloadNext();
    }

    public void ClearQueue()
    {
        if (_current == null) { Queue.Clear(); _originalOrder.Clear(); return; }
        var keep = _current;
        Queue.Clear();
        Queue.Add(keep);
        _originalOrder = new List<Track> { keep };
        CurrentIndex = 0;
        PreloadNext();
    }

    public void StopAndClear()
    {
        _loadToken++;
        _engine.Stop();
        _ = _sp.DeactivateAsync(pause: true);
        _loopback.Stop();
        IsPlaying = false;
        Queue.Clear();
        _originalOrder.Clear();
        CurrentIndex = -1;
        CurrentTrack = null;
        CurrentArt = null;
        CurrentArtPath = null;
        PositionSeconds = 0;
        DurationSeconds = 0;
    }

    /// <summary>Applies ReplayGain settings to the playing track (and future tracks).</summary>
    public void RefreshReplayGain()
    {
        if (_current != null) _engine.SetCurrentGain(ReplayGainFor(_current));
        PreloadNext();
    }

    // ---- Internals ---------------------------------------------------------------------------

    private void ReplaceQueue(List<Track> list)
    {
        Queue.Clear();
        foreach (var t in list) Queue.Add(t);
    }

    private List<Track> ShuffledWithFirst(List<Track> list, Track? first)
    {
        var rest = list.Where(t => !ReferenceEquals(t, first)).ToList();
        for (int i = rest.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }
        if (first != null) rest.Insert(0, first);
        return rest;
    }

    private void Reorder()
    {
        if (Queue.Count < 2) return;
        if (_shuffle)
        {
            _originalOrder = Queue.ToList();
            ReplaceQueue(ShuffledWithFirst(Queue.ToList(), _current));
        }
        else
        {
            var set = new HashSet<Track>(Queue);
            var restored = _originalOrder.Where(set.Contains).Distinct().ToList();
            foreach (var t in Queue) if (!restored.Contains(t)) restored.Add(t);
            ReplaceQueue(restored);
        }
        if (_current != null) CurrentIndex = Queue.IndexOf(_current);
        PreloadNext();
    }

    private float ReplayGainFor(Track t)
    {
        var mode = _settings.Current.ReplayGain;
        if (mode == ReplayGainMode.Off) return 1f;
        double? gain = mode == ReplayGainMode.Album ? t.RgAlbumGain ?? t.RgTrackGain : t.RgTrackGain ?? t.RgAlbumGain;
        double? peak = mode == ReplayGainMode.Album ? t.RgAlbumPeak ?? t.RgTrackPeak : t.RgTrackPeak ?? t.RgAlbumPeak;
        if (gain == null) return 1f;
        double lin = Math.Pow(10, gain.Value / 20.0);
        if (peak is > 0) lin = Math.Min(lin, 1.0 / peak.Value);
        return (float)lin;
    }

    private async Task LoadAsync(int index, bool play, TimeSpan startAt)
    {
        if (index < 0 || index >= Queue.Count) return;
        int token = ++_loadToken;
        _preloadToken++;
        var track = Queue[index];
        CurrentIndex = index;
        // Reset the clock before announcing the new track, so listeners (Windows media flyout) never
        // pair the new song with the previous song's position.
        PositionSeconds = startAt.TotalSeconds;
        DurationSeconds = track.Duration.TotalSeconds;
        SetCurrent(track);
        _countedThisPlay = false;

        if (track.IsSpotify)
        {
            _engine.Stop();
            if (play) await StartSpotifyAsync(index, startAt.TotalSeconds, token);
            else
            {
                await _sp.DeactivateAsync(pause: true);
                _loopback.Stop();
                IsPlaying = false;
            }
            return;
        }

        if (_sp.Active)
        {
            await _sp.DeactivateAsync(pause: true);
            if (token != _loadToken) return;
        }
        _loopback.Stop();

        TrackSource? src = null;
        float gain = ReplayGainFor(track);
        int rate = _engine.SampleRate;
        try
        {
            src = await Task.Run(() =>
            {
                var s = TrackSource.Open(track, rate, gain);
                if (startAt > TimeSpan.Zero) s.Seek(startAt);
                return s;
            });
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            Notify?.Invoke($"Can't play “{track.Title}”: {ex.Message}");
            if (play && index + 1 < Queue.Count) await LoadAsync(index + 1, play, TimeSpan.Zero);
            else { _engine.Stop(); IsPlaying = false; }
            return;
        }

        if (token != _loadToken) { src.Dispose(); return; }

        DiagLog.Write($"Load '{track.Title}' play={play} len={src.Length}");
        _engine.Load(src, play);
        IsPlaying = play;
        if (src.Length > TimeSpan.Zero) DurationSeconds = src.Length.TotalSeconds;
        _countedThisPlay = false;
        PreloadNext();
    }

    private void SetCurrent(Track track)
    {
        CurrentTrack = track;
        var key = track.ArtKey;
        Task.Run(() =>
        {
            if (!_library.Art.Has(key))
            {
                if (track.IsSpotify)
                    _library.Art.EnsureFromUrlAsync(key, track.ArtUrl).GetAwaiter().GetResult();
                else
                {
                    using var tags = TryOpenTags(track.Path);
                    _library.Art.Ensure(key, track.Path, tags);
                }
            }
            return (img: _library.Art.LoadLarge(key), path: _library.Art.Has(key) ? _library.Art.LargePath(key) : null);
        }).ContinueWith(t =>
        {
            if (!ReferenceEquals(CurrentTrack, track)) return;
            CurrentArt = t.Result.img;
            CurrentArtPath = t.Result.path;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static TagLib.File? TryOpenTags(string path)
    {
        try { return TagLib.File.Create(path); } catch { return null; }
    }

    private int GetAutoNextIndex()
    {
        if (Queue.Count == 0 || _index < 0) return -1;
        if (_repeat == RepeatMode.One) return _index;
        int n = _index + 1;
        if (n >= Queue.Count) return _repeat == RepeatMode.All ? 0 : -1;
        return n;
    }

    private async void PreloadNext()
    {
        int token = ++_preloadToken;
        int ni = GetAutoNextIndex();
        // Spotify tracks aren't decoded by Flow; the hand-off happens when the local track ends.
        if (ni < 0 || _current == null || CurrentIsSpotify || Queue[ni].IsSpotify)
        {
            _nextIndex = -1; _nextTrack = null;
            _engine.SetNext(null);
            return;
        }
        var track = Queue[ni];
        int rate = _engine.SampleRate;
        float gain = ReplayGainFor(track);
        TrackSource? src = null;
        try { src = await Task.Run(() => TrackSource.Open(track, rate, gain)); }
        catch { /* will be reported when we actually try to play it */ }
        if (token != _preloadToken) { src?.Dispose(); return; }
        _nextIndex = ni;
        _nextTrack = track;
        _engine.SetNext(src);
    }

    private void OnEngineAdvanced(TrackSource src)
    {
        int idx = _nextIndex >= 0 && _nextIndex < Queue.Count && ReferenceEquals(Queue[_nextIndex], src.Track)
            ? _nextIndex
            : Queue.IndexOf(src.Track);
        if (idx < 0) idx = 0;
        CurrentIndex = idx;
        DurationSeconds = src.Length.TotalSeconds;
        PositionSeconds = 0;
        SetCurrent(src.Track);
        _countedThisPlay = false;
        PreloadNext();
    }

    private void OnEngineEnded()
    {
        DiagLog.Write($"Engine ended at {PositionSeconds:0.0}s of {DurationSeconds:0.0}s");
        int next = GetAutoNextIndex();
        if (next >= 0 && Queue[next].IsSpotify)
        {
            _ = LoadAsync(next, true, TimeSpan.Zero);
            return;
        }
        IsPlaying = false;
        _engine.Pause();
        // Park on the first track of the queue, ready to play again.
        if (Queue.Count > 0) _ = LoadAsync(0, false, TimeSpan.Zero);
    }

    private void Tick()
    {
        if (_current == null) return;
        double pos;
        if (CurrentIsSpotify)
        {
            _loopback.ClearIfStale();
            if (!_sp.Active) return;
            pos = _sp.PositionSeconds;
            if (_sp.DurationSeconds > 0) DurationSeconds = _sp.DurationSeconds;
        }
        else pos = _engine.Position.TotalSeconds;

        if (!IsUserSeeking && IsPlaying)
        {
            PositionSeconds = pos;
            if (!_countedThisPlay && DurationSeconds > 0 && pos >= Math.Min(30, DurationSeconds * 0.5))
            {
                _countedThisPlay = true;
                _current.PlayCount++;
                _current.LastPlayed = DateTime.Now;
                _library.SaveStats(_current);
            }
        }
    }

    // ---- Persistence -------------------------------------------------------------------------

    public void SaveState()
    {
        var s = _settings.Current;
        s.LastQueue = Queue.Select(t => t.Path).Take(5000).ToList();
        s.LastIndex = _index;
        s.LastPosition = PositionSeconds;
    }

    public void Restore()
    {
        var s = _settings.Current;
        if (!s.ResumeOnStart || s.LastQueue.Count == 0 || s.LastIndex < 0) return;
        var paths = s.LastQueue.ToList();
        int index = s.LastIndex;
        double pos = s.LastPosition;
        Task.Run(() =>
        {
            var list = new List<Track>();
            int newIndex = -1;
            for (int i = 0; i < paths.Count; i++)
            {
                var t = _library.Find(paths[i]) ?? (i == index || paths.Count < 300 ? _library.GetOrRead(paths[i]) : null);
                if (t == null) continue;
                if (i == index) newIndex = list.Count;
                list.Add(t);
            }
            return (list, newIndex);
        }).ContinueWith(t =>
        {
            var (list, idx) = t.Result;
            if (list.Count == 0 || HasTrack) return;
            if (idx < 0) { idx = 0; pos = 0; }
            _originalOrder = list.ToList();
            ReplaceQueue(list);
            _ = LoadAsync(idx, false, TimeSpan.FromSeconds(pos));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- Spotify ------------------------------------------------------------------------------

    /// <summary>The consecutive Spotify tracks starting at index (handed to Spotify in one go).</summary>
    /// <summary>
    /// Playback is started inside the track's album (some Spotify clients ignore bare track URIs),
    /// so a run is the current track plus following queue items that are simply the album's next tracks.
    /// Spotify then plays them gaplessly; anything else is handed over by Flow when the run ends.
    /// </summary>
    private List<string> BuildSpotifyRun(int index)
    {
        var first = Queue[index];
        var run = new List<string> { first.SpotifyUri! };
        if (_repeat == RepeatMode.One || first.SpotifyAlbumUri == null) return run;
        var prev = first;
        for (int i = index + 1; i < Queue.Count && run.Count < 50; i++)
        {
            var t = Queue[i];
            bool nextOnAlbum = t.IsSpotify && t.SpotifyAlbumUri == first.SpotifyAlbumUri &&
                               ((t.DiscNumber == prev.DiscNumber && t.TrackNumber == prev.TrackNumber + 1) ||
                                (t.DiscNumber == prev.DiscNumber + 1 && t.TrackNumber == 1));
            if (!nextOnAlbum) break;
            run.Add(t.SpotifyUri!);
            prev = t;
        }
        return run;
    }

    private async Task StartSpotifyAsync(int index, double startSeconds, int token)
    {
        IsPlaying = true;
        _loopback.Start();
        var err = await _sp.PlayAsync(BuildSpotifyRun(index), Queue[index].SpotifyAlbumUri, startSeconds,
            (long)Queue[index].Duration.TotalMilliseconds);
        if (token != _loadToken) return;
        if (err != null)
        {
            IsPlaying = false;
            _loopback.Stop();
            Notify?.Invoke(err);
            return;
        }
        OnPropertyChanged(nameof(PlayingOnText));
    }

    private async void ResumeSpotify()
    {
        if (_sp.Active)
        {
            IsPlaying = true;
            _loopback.Start();
            if (await _sp.ResumeAsync()) return;
        }
        // Not started yet (restored session) or Spotify lost the context - start the run again here.
        int token = ++_loadToken;
        await StartSpotifyAsync(_index, PositionSeconds, token);
    }

    private void OnSpotifyItemChanged(string uri)
    {
        int idx = -1;
        for (int i = Math.Max(0, _index); i < Queue.Count; i++)
            if (Queue[i].Path == uri) { idx = i; break; }
        if (idx < 0) idx = Queue.ToList().FindIndex(t => t.Path == uri);
        if (idx < 0) return;
        CurrentIndex = idx;
        DurationSeconds = Queue[idx].Duration.TotalSeconds;
        PositionSeconds = 0;
        SetCurrent(Queue[idx]);
        _countedThisPlay = false;
    }

    private void OnSpotifyRunEnded()
    {
        int next = GetAutoNextIndex();
        // Spotify may already be auto-playing something of its own; silence it unless we hand it the next song.
        if (next < 0 || !Queue[next].IsSpotify) _ = _sp.PauseRemoteAsync();
        if (next >= 0) { _ = LoadAsync(next, true, TimeSpan.Zero); return; }
        IsPlaying = false;
        _loopback.Stop();
        if (Queue.Count > 0) _ = LoadAsync(0, false, TimeSpan.Zero);
    }

    public void Dispose()
    {
        _timer.Stop();
        if (CurrentIsSpotify && _sp.Active && _sp.IsPlaying) _sp.PauseOnExit();
        _loopback.Dispose();
        _engine.Dispose();
    }
}
