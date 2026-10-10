using NAudio.Wave;

namespace Flow.Audio;

/// <summary>
/// A live 32-bit float PCM stream (built-in Spotify playback): one thread writes what librespot outputs,
/// the audio engine reads it. librespot's pipe output is not paced to real time (it decodes as fast as the pipe
/// accepts), so a full buffer makes the writer wait: Flow's playback clock paces librespot, and the frames
/// read here are an exact playback position. Underruns read as silence so the output never stops.
/// </summary>
public sealed class LiveInput : ISampleProvider
{
    private readonly float[] _ring;
    private readonly int _channels;
    private long _written, _read;          // total samples written/read (single writer, single reader)
    private volatile bool _flushRequested;
    private readonly byte[] _carry = new byte[4];
    private int _carryCount;
    private long _framesConsumed;

    public LiveInput(int sampleRate, int channels, double bufferSeconds = 1.0)
    {
        _channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _ring = new float[(int)(sampleRate * channels * bufferSeconds)];
    }

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
        while (Volatile.Read(ref _written) - Volatile.Read(ref _read) >= _ring.Length)
        {
            if (!Attached) { Interlocked.Increment(ref _read); break; }
            if (++spins > 3) Thread.Sleep(2); else Thread.Yield();
        }
        long w = _written;
        _ring[w % _ring.Length] = sample;
        Volatile.Write(ref _written, w + 1);
    }

    /// <summary>Reader side (audio thread): never blocks; silence on underrun.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        if (_flushRequested)
        {
            _flushRequested = false;
            Volatile.Write(ref _read, Volatile.Read(ref _written));
        }
        long r = Volatile.Read(ref _read);
        long available = Volatile.Read(ref _written) - r;
        int n = (int)Math.Min(available, count);
        n -= n % _channels;
        for (int k = 0; k < n; k++) buffer[offset + k] = _ring[(r + k) % _ring.Length];
        Volatile.Write(ref _read, r + n);
        if (n < count) Array.Clear(buffer, offset + n, count - n);
        Interlocked.Add(ref _framesConsumed, n / _channels);
        return count;
    }
}
