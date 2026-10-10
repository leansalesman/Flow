using NAudio.Dsp;

namespace Flow.Audio;

/// <summary>10-band stereo peaking equalizer. Gains may be changed from any thread.</summary>
public sealed class Equalizer
{
    public static readonly float[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    private readonly int _sampleRate;
    private readonly BiQuadFilter[,] _filters;
    private readonly float[] _gains = new float[Frequencies.Length];
    private volatile bool _dirty = true;
    private float _preamp = 1f;

    public Equalizer(int sampleRate)
    {
        _sampleRate = sampleRate;
        _filters = new BiQuadFilter[2, Frequencies.Length];
        for (int c = 0; c < 2; c++)
            for (int b = 0; b < Frequencies.Length; b++)
                _filters[c, b] = BiQuadFilter.PeakingEQ(sampleRate, Frequencies[b], 1.0f, 0f);
    }

    public bool Enabled { get; set; }

    public void SetGain(int band, double db)
    {
        _gains[band] = (float)Math.Clamp(db, -12, 12);
        _dirty = true;
    }

    public void SetGains(IReadOnlyList<double> gains)
    {
        for (int i = 0; i < Math.Min(gains.Count, _gains.Length); i++)
            _gains[i] = (float)Math.Clamp(gains[i], -12, 12);
        _dirty = true;
    }

    public void Process(float[] buffer, int offset, int count)
    {
        if (!Enabled) return;
        if (_dirty)
        {
            _dirty = false;
            float max = 0;
            for (int b = 0; b < _gains.Length; b++)
            {
                float f = Math.Min(Frequencies[b], _sampleRate * 0.45f);
                for (int c = 0; c < 2; c++) _filters[c, b].SetPeakingEq(_sampleRate, f, 1.0f, _gains[b]);
                max = Math.Max(max, _gains[b]);
            }
            // Pull the level down a little when boosting so the EQ doesn't clip.
            _preamp = (float)Math.Pow(10, -max * 0.6 / 20.0);
        }

        bool any = false;
        for (int b = 0; b < _gains.Length; b++) if (Math.Abs(_gains[b]) > 0.01f) { any = true; break; }
        if (!any) return;

        int bands = _gains.Length;
        for (int i = 0; i < count; i += 2)
        {
            float l = buffer[offset + i] * _preamp;
            float r = buffer[offset + i + 1] * _preamp;
            for (int b = 0; b < bands; b++)
            {
                if (_gains[b] == 0) continue;
                l = _filters[0, b].Transform(l);
                r = _filters[1, b].Transform(r);
            }
            if (!float.IsFinite(l) || !float.IsFinite(r))
            {
                // A bad sample poisons the filters' memory and they'd stay silent: start them fresh.
                for (int c = 0; c < 2; c++)
                    for (int b = 0; b < bands; b++)
                        _filters[c, b] = BiQuadFilter.PeakingEQ(_sampleRate, Math.Min(Frequencies[b], _sampleRate * 0.45f), 1.0f, _gains[b]);
                l = r = 0;
            }
            buffer[offset + i] = l;
            buffer[offset + i + 1] = r;
        }
    }

    public static readonly IReadOnlyDictionary<string, double[]> Presets = new Dictionary<string, double[]>
    {
        ["Flat"]         = new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        ["Bass Boost"]   = new double[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 },
        ["Treble Boost"] = new double[] { 0, 0, 0, 0, 0, 1, 2, 4, 5, 6 },
        ["Vocal"]        = new double[] { -2, -2, -1, 1, 3, 4, 3, 1, 0, -1 },
        ["Rock"]         = new double[] { 4, 3, 2, 0, -1, -1, 1, 2, 3, 4 },
        ["Pop"]          = new double[] { -1, 1, 3, 4, 3, 0, -1, -1, 0, 1 },
        ["Jazz"]         = new double[] { 3, 2, 1, 2, -1, -1, 0, 1, 2, 3 },
        ["Classical"]    = new double[] { 4, 3, 2, 1, -1, -1, 0, 2, 3, 4 },
        ["Electronic"]   = new double[] { 5, 4, 1, 0, -2, 1, 0, 1, 4, 5 },
        ["Loudness"]     = new double[] { 5, 3, 0, 0, -1, 0, -1, 0, 3, 4 },
    };
}
