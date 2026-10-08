using Flow.Visualizer;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Flow.Audio;

/// <summary>
/// Owns the WASAPI output and the mixing pipeline:
/// current/next/outgoing track sources -> crossfade/gapless -> EQ -> visualizer tap -> volume -> soft clip.
/// Events are raised on the audio thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _lock = new();
    private readonly Pipeline _pipe;
    private IWavePlayer? _out;
    private MMDeviceEnumerator? _enumerator;
    private DeviceNotifier? _notifier;
    private bool _wantPlaying;

    public event Action<TrackSource>? TrackAdvanced;
    public event Action? PlaybackEnded;
    public event Action<string>? OutputError;

    public SpectrumAnalyzer Analyzer { get; } = new();
    public Equalizer Equalizer { get; }
    public int SampleRate { get; }

    public AudioEngine()
    {
        int rate = 48000;
        try
        {
            _enumerator = new MMDeviceEnumerator();
            using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            rate = dev.AudioClient.MixFormat.SampleRate;
        }
        catch { /* no device yet - use 48k */ }

        SampleRate = rate;
        Analyzer.SampleRate = rate;
        Equalizer = new Equalizer(rate);
        _pipe = new Pipeline(this, rate);
        CreateOutput();

        try
        {
            _notifier = new DeviceNotifier(this);
            _enumerator?.RegisterEndpointNotificationCallback(_notifier);
        }
        catch { /* not critical */ }
    }

    private void CreateOutput()
    {
        try
        {
            var o = new WasapiOut(AudioClientShareMode.Shared, true, 60);
            o.Init(_pipe);
            o.PlaybackStopped += OnPlaybackStopped;
            _out = o;
        }
        catch (Exception ex)
        {
            _out = null;
            OutputError?.Invoke("No audio output device available: " + ex.Message);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Infrastructure.DiagLog.Write("WASAPI stopped: " + (e.Exception?.ToString() ?? "no exception"));
        if (e.Exception != null && sender == _out)
            ThreadPool.QueueUserWorkItem(_ => ReinitOutput());
    }

    /// <summary>Re-creates the output (default device changed or device lost), preserving play state.</summary>
    public void ReinitOutput()
    {
        lock (_lock)
        {
            var old = _out;
            _out = null;
            if (old != null)
            {
                old.PlaybackStopped -= OnPlaybackStopped;
                try { old.Stop(); } catch { }
                try { old.Dispose(); } catch { }
            }
            CreateOutput();
            if (_wantPlaying) try { _out?.Play(); } catch { }
        }
    }

    // ---- Transport -------------------------------------------------------------------------

    public float Volume { get => _pipe.TargetVolume; set => _pipe.TargetVolume = Math.Clamp(value, 0f, 1f); }
    public double CrossfadeSeconds { get => _pipe.CrossfadeSeconds; set => _pipe.CrossfadeSeconds = Math.Clamp(value, 0, 12); }
    public bool Gapless { get => _pipe.Gapless; set => _pipe.Gapless = value; }

    public bool IsPlaying => _wantPlaying;

    /// <summary>Replace the current track (user-initiated). Disposes previous sources.</summary>
    public void Load(TrackSource source, bool play)
    {
        _pipe.SetCurrent(source);
        if (play) Play(); else Pause();
    }

    public void SetNext(TrackSource? next) => _pipe.SetNext(next);

    /// <summary>
    /// Plays a live stream (built-in Spotify playback) instead of local tracks, through the same EQ, analyzer,
    /// volume and limiter. Setting it stops local tracks; loading a local track (or Stop) clears it. Null detaches.
    /// </summary>
    public void SetLiveInput(LiveInput? input) => _pipe.SetLive(input);
    public bool HasLiveInput => _pipe.HasLive;

    public void Play()
    {
        lock (_lock)
        {
            _wantPlaying = true;
            if (_out == null) CreateOutput();
            try { _out?.Play(); } catch (Exception ex) { OutputError?.Invoke(ex.Message); }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _wantPlaying = false;
            try { _out?.Pause(); } catch { }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _wantPlaying = false;
            try { _out?.Stop(); } catch { }
        }
        _pipe.Clear();
        Analyzer.Clear();
    }

    public void Seek(TimeSpan position) => _pipe.Seek(position);
    public TimeSpan Position => _pipe.Position;
    public TimeSpan Length => _pipe.Length;
    public void SetCurrentGain(float gain) => _pipe.SetCurrentGain(gain);

    public void Dispose()
    {
        try { if (_notifier != null) _enumerator?.UnregisterEndpointNotificationCallback(_notifier); } catch { }
        lock (_lock)
        {
            if (_out != null)
            {
                _out.PlaybackStopped -= OnPlaybackStopped;
                try { _out.Stop(); } catch { }
                _out.Dispose();
                _out = null;
            }
        }
        _pipe.Clear();
        _enumerator?.Dispose();
    }

    // ---- Pipeline -------------------------------------------------------------------------

    private sealed class Pipeline : ISampleProvider
    {
        private readonly AudioEngine _owner;
        private readonly object _sync = new();
        private readonly int _rate;
        private TrackSource? _current, _next, _outgoing;
        private LiveInput? _liveSource;
        private ISampleProvider? _live;      // _liveSource, resampled to the engine rate when needed
        private long _fadeTotal, _fadePos;
        private int _gapFrames;
        private float[] _tmp = new float[8192];
        private float _vol = 1f;

        public float TargetVolume = 0.8f;
        public double CrossfadeSeconds;
        public bool Gapless = true;

        public Pipeline(AudioEngine owner, int rate)
        {
            _owner = owner;
            _rate = rate;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public void SetCurrent(TrackSource src)
        {
            TrackSource? a, b, c;
            lock (_sync)
            {
                a = _current; b = _outgoing; c = _next;
                _current = src; _outgoing = null; _next = null; _gapFrames = 0;
                DetachLive();
            }
            DisposeLater(a, b, c);
        }

        public bool HasLive { get { lock (_sync) return _live != null; } }

        public void SetLive(LiveInput? input)
        {
            TrackSource? a = null, b = null, c = null;
            lock (_sync)
            {
                if (ReferenceEquals(input, _liveSource)) return;
                DetachLive();
                if (input == null) return;
                a = _current; b = _outgoing; c = _next;
                _current = _outgoing = _next = null;
                _gapFrames = 0;
                _liveSource = input;
                _live = input.WaveFormat.SampleRate == _rate
                    ? input
                    : new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(input, _rate);
                input.Attached = true;
            }
            DisposeLater(a, b, c);
        }

        private void DetachLive()
        {
            if (_liveSource != null) _liveSource.Attached = false;
            _liveSource = null;
            _live = null;
        }

        public void SetNext(TrackSource? next)
        {
            TrackSource? old;
            lock (_sync) { old = _next; _next = next; }
            if (!ReferenceEquals(old, next)) DisposeLater(old);
        }

        public void SetCurrentGain(float gain)
        {
            lock (_sync) { if (_current != null) _current.Gain = gain; }
        }

        public void Clear()
        {
            TrackSource? a, b, c;
            lock (_sync)
            {
                a = _current; b = _outgoing; c = _next;
                _current = _outgoing = _next = null;
                DetachLive();
            }
            DisposeLater(a, b, c);
        }

        public void Seek(TimeSpan t)
        {
            TrackSource? o;
            lock (_sync)
            {
                o = _outgoing; _outgoing = null; _gapFrames = 0;
                try { _current?.Seek(t); } catch { }
            }
            DisposeLater(o);
        }

        public TimeSpan Position { get { lock (_sync) return _current?.Position ?? TimeSpan.Zero; } }
        public TimeSpan Length { get { lock (_sync) return _current?.Length ?? TimeSpan.Zero; } }

        private static void DisposeLater(params TrackSource?[] sources)
        {
            foreach (var s in sources)
                if (s != null) ThreadPool.QueueUserWorkItem(_ => s.Dispose());
        }

        public int Read(float[] buffer, int offset, int count)
        {
            TrackSource? advancedTo = null;
            bool ended = false;
            var toDispose = new List<TrackSource>(2);

            lock (_sync)
            {
                if (_live != null)
                {
                    // Live stream: LiveInput never blocks and pads underruns with silence.
                    int got = 0;
                    try { got = _live.Read(buffer, offset, count); } catch { }
                    if (got < count) Array.Clear(buffer, offset + got, count - got);
                }
                else
                {
                    int filled = 0;
                    int guard = 0;
                    while (filled < count && guard++ < 8)
                    {
                        if (_gapFrames > 0)
                        {
                            int n = Math.Min(_gapFrames * 2, count - filled);
                            Array.Clear(buffer, offset + filled, n);
                            filled += n;
                            _gapFrames -= n / 2;
                            continue;
                        }
                        if (_current == null) break;

                        // Start a crossfade when the current track nears its end and the next one is ready.
                        if (CrossfadeSeconds > 0.05 && _next != null && _outgoing == null)
                        {
                            var len = _current.Length;
                            var remaining = len - _current.Position;
                            if (len.TotalSeconds > CrossfadeSeconds * 2 && remaining.TotalSeconds <= CrossfadeSeconds)
                            {
                                _outgoing = _current;
                                _fadeTotal = Math.Max(1, (long)(remaining.TotalSeconds * _rate));
                                _fadePos = 0;
                                _current = _next;
                                _next = null;
                                advancedTo = _current;
                            }
                        }

                        int want = count - filled;
                        int got;
                        try { got = _current.Read(buffer, offset + filled, want); }
                        catch { got = 0; }

                        if (_outgoing != null && got > 0) MixOutgoing(buffer, offset + filled, got, toDispose);

                        filled += got;
                        if (got == 0)
                        {
                            // Current track finished.
                            toDispose.Add(_current);
                            if (_outgoing != null) { toDispose.Add(_outgoing); _outgoing = null; }
                            if (_next != null)
                            {
                                _current = _next;
                                _next = null;
                                advancedTo = _current;
                                if (!Gapless) _gapFrames = _rate * 4 / 10; // 400 ms pause between tracks
                            }
                            else
                            {
                                _current = null;
                                ended = true;
                            }
                        }
                    }

                    if (filled < count) Array.Clear(buffer, offset + filled, count - filled);
                }
            }

            _owner.Equalizer.Process(buffer, offset, count);
            _owner.Analyzer.Write(buffer, offset, count);
            ApplyVolumeAndLimit(buffer, offset, count);

            foreach (var s in toDispose) DisposeLater(s);
            if (advancedTo != null) _owner.TrackAdvanced?.Invoke(advancedTo);
            if (ended) _owner.PlaybackEnded?.Invoke();
            return count;
        }

        private void MixOutgoing(float[] buffer, int offset, int count, List<TrackSource> toDispose)
        {
            if (_tmp.Length < count) _tmp = new float[count];
            int got;
            try { got = _outgoing!.Read(_tmp, 0, count); } catch { got = 0; }
            if (got < count) Array.Clear(_tmp, got, count - got);
            for (int i = 0; i < count; i += 2)
            {
                double t = Math.Min(1.0, (double)(_fadePos + i / 2) / _fadeTotal);
                float gin = (float)Math.Sin(t * Math.PI / 2);
                float gout = (float)Math.Cos(t * Math.PI / 2);
                buffer[offset + i] = buffer[offset + i] * gin + _tmp[i] * gout;
                buffer[offset + i + 1] = buffer[offset + i + 1] * gin + _tmp[i + 1] * gout;
            }
            _fadePos += count / 2;
            if (got == 0 || _fadePos >= _fadeTotal)
            {
                toDispose.Add(_outgoing!);
                _outgoing = null;
            }
        }

        private void ApplyVolumeAndLimit(float[] buffer, int offset, int count)
        {
            float target = TargetVolume * TargetVolume; // perceptual curve
            float start = _vol;
            int frames = Math.Max(1, count / 2);
            for (int i = 0; i < count; i += 2)
            {
                float g = start + (target - start) * ((i / 2f) / frames);
                buffer[offset + i] = SoftClip(buffer[offset + i] * g);
                buffer[offset + i + 1] = SoftClip(buffer[offset + i + 1] * g);
            }
            _vol = target;
        }

        private static float SoftClip(float x)
        {
            const float knee = 0.95f;
            if (x > knee) return knee + (1 - knee) * MathF.Tanh((x - knee) / (1 - knee));
            if (x < -knee) return -knee - (1 - knee) * MathF.Tanh((-x - knee) / (1 - knee));
            return x;
        }
    }

    private sealed class DeviceNotifier : IMMNotificationClient
    {
        private readonly AudioEngine _engine;
        private DateTime _last;
        public DeviceNotifier(AudioEngine engine) => _engine = engine;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Render || role != Role.Multimedia) return;
            var now = DateTime.UtcNow;
            if ((now - _last).TotalMilliseconds < 300) return;
            _last = now;
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(150); _engine.ReinitOutput(); });
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
