using NAudio.Dsp;

namespace Flow.Visualizer;

/// <summary>
/// Receives audio from the playback thread into a ring buffer and, on demand (UI thread),
/// produces log-spaced frequency band levels normalized to 0..1.
/// </summary>
public sealed class SpectrumAnalyzer
{
    private const int FftSize = 4096;
    private const int FftM = 12; // 2^12 = 4096

    private readonly float[] _ring = new float[FftSize];
    private int _write;
    private readonly object _lock = new();

    private readonly float[] _window = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _mag = new float[FftSize / 2];
    private readonly float[] _snapshot = new float[FftSize];

    private int[]? _bandLo, _bandHi;
    private float[]? _bandTilt;
    private int _bandCount;

    public int SampleRate { get; set; } = 48000;

    public SpectrumAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1))));
    }

    /// <summary>Called from the audio thread with interleaved stereo samples.</summary>
    public void Write(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            int w = _write;
            for (int i = 0; i < count; i += 2)
            {
                _ring[w] = (buffer[offset + i] + buffer[offset + i + 1]) * 0.5f;
                w = (w + 1) & (FftSize - 1);
            }
            _write = w;
        }
    }

    /// <summary>Copies the most recent mono samples (oldest first) for waveform drawing.</summary>
    public void GetWaveform(float[] dest)
    {
        int n = Math.Min(dest.Length, FftSize);
        lock (_lock)
        {
            int start = (_write - n + FftSize) & (FftSize - 1);
            for (int i = 0; i < n; i++) dest[i] = _ring[(start + i) & (FftSize - 1)];
        }
    }

    public void Clear()
    {
        lock (_lock) { Array.Clear(_ring); }
    }

    /// <summary>Fills <paramref name="levels"/> (0..1) with band energies, bass first.</summary>
    public void Compute(float[] levels, int bandCount)
    {
        if (bandCount <= 0) return;
        if (bandCount != _bandCount || _bandLo == null || _builtRate != SampleRate) BuildBands(bandCount);

        lock (_lock)
        {
            int start = _write;
            int first = FftSize - start;
            Array.Copy(_ring, start, _snapshot, 0, first);
            Array.Copy(_ring, 0, _snapshot, first, start);
        }

        // Silence (paused, gaps, quiet intros): nothing to analyze, skip the FFT.
        float loudest = 0;
        for (int i = 0; i < FftSize; i++) loudest = Math.Max(loudest, Math.Abs(_snapshot[i]));
        if (loudest < 1e-5f)
        {
            Array.Clear(levels, 0, Math.Min(bandCount, levels.Length));
            return;
        }

        for (int i = 0; i < FftSize; i++)
        {
            _fft[i].X = _snapshot[i] * _window[i];
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftM, _fft);
        for (int i = 0; i < _mag.Length; i++)
            _mag[i] = MathF.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);

        const float floorDb = -62f, ceilDb = -6f;
        for (int b = 0; b < bandCount; b++)
        {
            float peak = 0;
            for (int k = _bandLo![b]; k <= _bandHi![b]; k++) if (_mag[k] > peak) peak = _mag[k];
            // NAudio's forward FFT scales by 1/N; Hann window halves amplitude -> x4 approximates full scale.
            float db = 20f * MathF.Log10(peak * 4f + 1e-9f) + _bandTilt![b];
            float v = (db - floorDb) / (ceilDb - floorDb);
            levels[b] = Math.Clamp(v, 0f, 1f);
        }
    }

    private int _builtRate;

    private void BuildBands(int count)
    {
        _bandCount = count;
        _builtRate = SampleRate;
        _bandLo = new int[count];
        _bandHi = new int[count];
        _bandTilt = new float[count];
        double fMin = 40, fMax = Math.Min(16000, SampleRate / 2.0 - 100);
        double binHz = (double)SampleRate / FftSize;
        for (int b = 0; b < count; b++)
        {
            double lo = fMin * Math.Pow(fMax / fMin, (double)b / count);
            double hi = fMin * Math.Pow(fMax / fMin, (double)(b + 1) / count);
            int kLo = Math.Max(1, (int)Math.Floor(lo / binHz));
            int kHi = Math.Max(kLo, (int)Math.Ceiling(hi / binHz) - 1);
            _bandLo[b] = Math.Min(kLo, _mag.Length - 1);
            _bandHi[b] = Math.Min(kHi, _mag.Length - 1);
            // Music energy falls ~3-4.5 dB/octave; tilt it back up so treble is visible.
            double center = Math.Sqrt(lo * hi);
            _bandTilt[b] = (float)(3.0 * Math.Log2(Math.Max(center, 60) / 60.0));
        }
    }
}
