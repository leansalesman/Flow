using System.IO;
using Flow.Library;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Flow.Audio;

/// <summary>
/// One decoded track, converted to stereo float at the engine sample rate.
/// Not thread-safe; the engine serializes access under its lock.
/// </summary>
public sealed class TrackSource : IDisposable
{
    private readonly WaveStream _stream;
    private readonly int _outRate;
    private ISampleProvider _chain = null!;

    public Track Track { get; }
    public float Gain { get; set; } = 1f;

    private TrackSource(Track track, WaveStream stream, int outRate)
    {
        Track = track;
        _stream = stream;
        _outRate = outRate;
        BuildChain();
    }

    public static TrackSource Open(Track track, int outRate, float gain)
    {
        var stream = OpenDecoder(track.Path);
        return new TrackSource(track, stream, outRate) { Gain = gain };
    }

    public static WaveStream OpenDecoder(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".opus":
                return new OpusWaveStream(path);
            case ".ogg":
            case ".oga":
                try { return new VorbisWaveReader(path); }
                catch { return new OpusWaveStream(path); }
            case ".aif":
            case ".aiff":
            case ".aifc":
                return new AiffFileReader(path);
            case ".wav":
                try
                {
                    var w = new WaveFileReader(path);
                    var enc = w.WaveFormat.Encoding;
                    if (enc is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
                        return w;
                    w.Dispose();
                }
                catch { /* fall back to Media Foundation */ }
                return OpenMediaFoundation(path);
            default:
                // MP3, AAC/M4A, ALAC, FLAC, WMA - all decoded natively by Windows Media Foundation.
                return OpenMediaFoundation(path);
        }
    }

    private static WaveStream OpenMediaFoundation(string path) =>
        new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RequestFloatOutput = true,
            RepositionInRead = true,
        });

    private void BuildChain()
    {
        ISampleProvider sp = _stream.ToSampleProvider();
        int ch = sp.WaveFormat.Channels;
        if (ch == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (ch > 2) sp = new StereoDownmixProvider(sp);
        if (sp.WaveFormat.SampleRate != _outRate) sp = new WdlResamplingSampleProvider(sp, _outRate);
        _chain = sp;
    }

    public TimeSpan Position => _stream.CurrentTime;

    public TimeSpan Length
    {
        get
        {
            var len = _stream.TotalTime;
            return len > TimeSpan.Zero ? len : Track.Duration;
        }
    }

    public void Seek(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var len = Length;
        if (len > TimeSpan.Zero && t > len) t = len;
        _stream.CurrentTime = t;
        BuildChain(); // drop any resampler history
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _chain.Read(buffer, offset, count);
        float g = Gain;
        if (g != 1f)
            for (int i = 0; i < n; i++) buffer[offset + i] *= g;
        return n;
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>Downmixes multichannel audio (5.1 etc.) to stereo.</summary>
internal sealed class StereoDownmixProvider : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly int _ch;
    private float[] _buf = Array.Empty<float>();

    public StereoDownmixProvider(ISampleProvider src)
    {
        _src = src;
        _ch = src.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        int need = frames * _ch;
        if (_buf.Length < need) _buf = new float[need];
        int read = _src.Read(_buf, 0, need);
        int got = read / _ch;
        for (int f = 0; f < got; f++)
        {
            int b = f * _ch;
            float l = _buf[b], r = _buf[b + 1];
            if (_ch >= 3) { float c = _buf[b + 2] * 0.707f; l += c; r += c; }
            if (_ch >= 6) { l += _buf[b + 4] * 0.5f; r += _buf[b + 5] * 0.5f; }
            buffer[offset + f * 2] = l * 0.6f;
            buffer[offset + f * 2 + 1] = r * 0.6f;
        }
        return got * 2;
    }
}
