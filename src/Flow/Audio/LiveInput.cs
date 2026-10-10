using NAudio.Wave;

namespace Flow.Audio;

/// <summary>
/// A live 32-bit float PCM stream (built-in Spotify playback): one thread writes what librespot outputs,
/// the audio engine reads it. librespot's pipe output is not paced to real time (it decodes as fast as the pipe
/// accepts), so a full buffer makes the writer wait: Flow's playback clock paces librespot, and the frames
/// read here are an exact playback position. Underruns read as silence so the output never stops.
/// <para>Song changes: librespot plays a list back to back in one stream. The host marks where the next song
/// begins (<see cref="SetBoundary"/>); this class then crossfades into it, or switches exactly there, and tells the
/// host at that moment. For a crossfade the writer may run ahead by the fade length plus a second, so the start of
/// the next song is already buffered while the end of this one plays.</para>
/// </summary>
public sealed class LiveInput : ISampleProvider
{
    private readonly float[] _ring;
    private readonly int _channels, _rate;
    private long _limit;                   // samples the writer may run ahead of the reader
    private long _written, _read;          // total samples written/read (single writer, single reader)
    private volatile bool _flushRequested;
    private readonly byte[] _carry = new byte[4];
    private int _carryCount;
    private long _framesConsumed;

    /// <param name="bufferSeconds">Room for the longest crossfade plus a second.</param>
    public LiveInput(int sampleRate, int channels, double bufferSeconds = 1.0)
    {
        _channels = channels;
        _rate = sampleRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _ring = new float[(int)(sampleRate * channels * bufferSeconds)];
        AheadSeconds = 1.0;
    }

    /// <summary>How far librespot may decode ahead of what is heard (the crossfade length plus a second).</summary>
    public double AheadSeconds
    {
        set => Volatile.Write(ref _limit, Math.Clamp((long)(value * _rate) * _channels, (long)_rate / 2 * _channels, _ring.Length));
    }

    /// <summary>Position in the stream (frames librespot has written so far); song boundaries are marked with it.</summary>
    public long WrittenFrames => Volatile.Read(ref _written) / _channels;

    public WaveFormat WaveFormat { get; }

    /// <summary>Frames played since the last <see cref="ResetPosition"/> (for an exact track position).</summary>
    public long FramesConsumed => Interlocked.Read(ref _framesConsumed);
    public double SecondsConsumed => FramesConsumed / (double)WaveFormat.SampleRate;
    public void ResetPosition() => Interlocked.Exchange(ref _framesConsumed, 0);

    /// <summary>Audio waiting in the buffer (how far librespot is ahead of what is heard).</summary>
    public double BufferedSeconds => Math.Max(0, Volatile.Read(ref _written) - Volatile.Read(ref _read)) / (double)(WaveFormat.SampleRate * _channels);

    /// <summary>Set while the engine is pulling from this input; while false a full buffer drops instead of waiting.</summary>
    public volatile bool Attached;

    /// <summary>Drops buffered audio (after play / seek / next / previous, so stale audio isn't heard).</summary>
    public void Flush() => _flushRequested = true;

    /// <summary>
    /// A new librespot process is about to write: forget any half-received sample from the old one and finish a
    /// half-written stereo frame. Without this, a restart (e.g. turning on volume normalisation mid-song) glued the
    /// new stream onto 1 to 3 stale bytes, so every sample after it was read misaligned: loud noise, and garbage
    /// values that could leave the equalizer silent. Writer thread only (call before the new reader starts).
    /// </summary>
    public void ResetStream()
    {
        _carryCount = 0;
        while (Volatile.Read(ref _written) % _channels != 0) Push(0f);
        Flush();
    }

    /// <summary>Writer side: raw little-endian float bytes from librespot's stdout.</summary>
    public void Write(byte[] bytes, int count)
    {
        int i = 0;
        // Complete a float split across reads.
        while (_carryCount > 0 && _carryCount < 4 && i < count) _carry[_carryCount++] = bytes[i++];
        if (_carryCount == 4) { Push(BitConverter.ToSingle(_carry, 0)); _carryCount = 0; }
        for (; i + 4 <= count; i += 4) Push(BitConverter.ToSingle(bytes, i));
        while (i < count) _carry[_carryCount++] = bytes[i++];
    }

    private void Push(float sample)
    {
        // Never pass on garbage: anything that isn't a sane sample becomes silence.
        if (!float.IsFinite(sample) || sample > 4f || sample < -4f) sample = 0f;
        // Wait for room (back-pressure on librespot); when nobody is reading, drop the oldest instead.
        int spins = 0;
        while (Volatile.Read(ref _written) - Volatile.Read(ref _read) >= Volatile.Read(ref _limit))
        {
            if (!Attached) { Interlocked.Increment(ref _read); break; }
            if (++spins > 3) Thread.Sleep(2); else Thread.Yield();
        }
        long w = _written;
        _ring[w % _ring.Length] = sample;
        Volatile.Write(ref _written, w + 1);
    }

    // ---- Song boundaries (positions in samples; guarded by _marks, which the audio thread holds only briefly) ----

    private readonly object _marks = new();
    private readonly List<(long At, Action Run)> _at = new();
    private long _boundary = -1, _fadeWanted;    // the next song's first sample; the fade length asked for
    private Action? _onSwitch;
    private bool _fading;
    private long _oldEnd, _fadeTotal, _fadeDone, _next;   // during a fade: where the old song ends, progress, new song's read position

    /// <summary>Runs <paramref name="run"/> (on the thread pool) once playback reaches the given stream frame.</summary>
    public void At(long frame, Action run)
    {
        lock (_marks) _at.Add((frame * _channels, run));
    }

    /// <summary>
    /// The next song starts at stream frame <paramref name="frame"/>. Crossfades into it over
    /// <paramref name="fadeSeconds"/> (0 = a plain gapless change) and runs <paramref name="onSwitch"/> (thread pool)
    /// when the next song becomes audible: at the start of the fade, or at the boundary itself. A fade that no longer
    /// fits is shortened, and skipped below a quarter of a second.
    /// </summary>
    public void SetBoundary(long frame, double fadeSeconds, Action onSwitch)
    {
        lock (_marks)
        {
            _boundary = frame * _channels;
            _fadeWanted = (long)(Math.Max(0, fadeSeconds) * _rate) * _channels;
            _onSwitch = onSwitch;
        }
    }

    private long _dropBefore = -1;
    private Action? _onDropped;

    /// <summary>
    /// librespot started writing from a new place (a seek, or a song Flow asked for) at stream frame
    /// <paramref name="frame"/>: skip everything before it, which is the old place's audio that arrived while the
    /// command was on its way, and run <paramref name="onDone"/> (thread pool) once playback is there.
    /// </summary>
    public void DropBefore(long frame, Action? onDone)
    {
        lock (_marks)
        {
            _dropBefore = frame * _channels;
            _onDropped = onDone;
        }
    }

    /// <summary>Reader side (audio thread): never blocks; silence on underrun.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        List<Action>? due = null;
        lock (_marks)
        {
            if (_flushRequested)
            {
                // An explicit change (play, seek, next): stale audio, and anything planned for it, goes.
                _flushRequested = false;
                Volatile.Write(ref _read, Volatile.Read(ref _written));
                _at.Clear();
                _boundary = -1;
                _onSwitch = null;
                _fading = false;
                _dropBefore = -1;
                _onDropped = null;
            }
            long r = Volatile.Read(ref _read);
            long w = Volatile.Read(ref _written);
            if (_dropBefore >= 0)
            {
                if (_dropBefore > r)
                {
                    r = Math.Min(_dropBefore, w);
                    _at.RemoveAll(m => m.At < r);
                    _boundary = -1;
                    _onSwitch = null;
                    _fading = false;
                }
                if (_onDropped != null) (due ??= new()).Add(_onDropped);
                _dropBefore = -1;
                _onDropped = null;
            }
            int ch = _channels, i = 0, frames = 0;
            while (i + ch <= count)
            {
                if (!_fading && _boundary >= 0 && r >= _boundary - _fadeWanted)
                {
                    long left = _boundary - r;
                    if (_fadeWanted > 0 && left >= (long)_rate / 4 * ch)
                    {
                        _fading = true;
                        _oldEnd = _boundary;
                        _fadeTotal = left;
                        _fadeDone = 0;
                        _next = _boundary;
                        (due ??= new()).Add(_onSwitch!);
                        _boundary = -1;
                        _onSwitch = null;
                    }
                    else if (r >= _boundary)
                    {
                        (due ??= new()).Add(_onSwitch!);
                        _boundary = -1;
                        _onSwitch = null;
                    }
                }
                if (_fading && r >= _oldEnd)
                {
                    // The old song is over: carry on in the new one after the part already heard.
                    _fading = false;
                    r = _next;
                }
                if (r + ch > w) break;
                if (_fading)
                {
                    double t = Math.Min(1.0, (double)_fadeDone / _fadeTotal);
                    float gin = (float)Math.Sin(t * Math.PI / 2), gout = (float)Math.Cos(t * Math.PI / 2);
                    bool haveNext = _next + ch <= w;
                    for (int c = 0; c < ch; c++)
                    {
                        float a = _ring[(r + c) % _ring.Length] * gout;
                        buffer[offset + i + c] = haveNext ? a + _ring[(_next + c) % _ring.Length] * gin : a;
                    }
                    if (haveNext) _next += ch;
                    _fadeDone += ch;
                }
                else
                {
                    for (int c = 0; c < ch; c++) buffer[offset + i + c] = _ring[(r + c) % _ring.Length];
                }
                r += ch;
                i += ch;
                frames++;
            }
            Volatile.Write(ref _read, r);
            if (i < count) Array.Clear(buffer, offset + i, count - i);
            Interlocked.Add(ref _framesConsumed, frames);
            for (int k = _at.Count - 1; k >= 0; k--)
                if (_at[k].At <= r) { (due ??= new()).Add(_at[k].Run); _at.RemoveAt(k); }
        }
        if (due != null)
            foreach (var a in due) ThreadPool.QueueUserWorkItem(_ => a());
        return count;
    }
}
