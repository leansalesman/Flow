using Flow.Visualizer;
using NAudio.Wave;

namespace Flow.Audio;

/// <summary>
/// Captures what the speakers are playing (WASAPI loopback) and feeds it to the visualizer.
/// Used while music plays outside Flow's own audio engine (Spotify).
/// </summary>
public sealed class LoopbackTap : IDisposable
{
    private readonly SpectrumAnalyzer _analyzer;
    private readonly int _engineRate;
    private WasapiLoopbackCapture? _capture;
    private float[] _stereo = Array.Empty<float>();
    private long _lastData;

    public LoopbackTap(SpectrumAnalyzer analyzer, int engineRate)
    {
        _analyzer = analyzer;
        _engineRate = engineRate;
    }

    public bool Running => _capture != null;

    public void Start()
    {
        if (_capture != null) return;
        try
        {
            var c = new WasapiLoopbackCapture();
            c.DataAvailable += OnData;
            c.RecordingStopped += (_, _) => { };
            _analyzer.SampleRate = c.WaveFormat.SampleRate;
            _analyzer.Clear();
            c.StartRecording();
            _capture = c;
        }
        catch { _capture = null; }
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        if (c != null)
        {
            c.DataAvailable -= OnData;
            try { c.StopRecording(); } catch { }
            c.Dispose();
        }
        _analyzer.Clear();
        _analyzer.SampleRate = _engineRate;
    }

    /// <summary>Loopback delivers nothing during silence; clear the analyzer so bars don't freeze.</summary>
    public void ClearIfStale()
    {
        if (_capture != null && Environment.TickCount64 - Interlocked.Read(ref _lastData) > 250) _analyzer.Clear();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var cap = _capture;
        if (cap == null || e.BytesRecorded == 0) return;
        var fmt = cap.WaveFormat;
        int ch = Math.Max(1, fmt.Channels);
        int frames;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);
        if (isFloat)
        {
            frames = e.BytesRecorded / (4 * ch);
            if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                float l = BitConverter.ToSingle(e.Buffer, (f * ch) * 4);
                float r = ch > 1 ? BitConverter.ToSingle(e.Buffer, (f * ch + 1) * 4) : l;
                _stereo[f * 2] = l;
                _stereo[f * 2 + 1] = r;
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            frames = e.BytesRecorded / (2 * ch);
            if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                float l = BitConverter.ToInt16(e.Buffer, (f * ch) * 2) / 32768f;
                float r = ch > 1 ? BitConverter.ToInt16(e.Buffer, (f * ch + 1) * 2) / 32768f : l;
                _stereo[f * 2] = l;
                _stereo[f * 2 + 1] = r;
            }
        }
        else return;

        Interlocked.Exchange(ref _lastData, Environment.TickCount64);
        _analyzer.Write(_stereo, 0, frames * 2);
    }

    public void Dispose() => Stop();
}
