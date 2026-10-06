using System.IO;
using Concentus;
using Concentus.Oggfile;
using NAudio.Wave;

namespace Flow.Audio;

/// <summary>Decodes Ogg Opus files to 48 kHz stereo 16-bit PCM.</summary>
public sealed class OpusWaveStream : WaveStream
{
    private readonly FileStream _file;
    private readonly OpusOggReadStream _reader;
    private readonly WaveFormat _format = new(48000, 16, 2);
    private short[]? _pending;
    private int _pendingOffset;
    private long _position;
    private readonly long _length;

    public OpusWaveStream(string path)
    {
        _file = File.OpenRead(path);
        var decoder = OpusCodecFactory.CreateDecoder(48000, 2);
        _reader = new OpusOggReadStream(decoder, _file);
        if (!_reader.HasNextPacket && !string.IsNullOrEmpty(_reader.LastError))
            throw new InvalidDataException(_reader.LastError);
        _length = (long)(_reader.TotalTime.TotalSeconds * _format.AverageBytesPerSecond);
        _length -= _length % _format.BlockAlign;
    }

    public override WaveFormat WaveFormat => _format;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            var t = TimeSpan.FromSeconds((double)Math.Clamp(value, 0, _length) / _format.AverageBytesPerSecond);
            _reader.SeekTo(t);
            _pending = null;
            _pendingOffset = 0;
            _position = value - value % _format.BlockAlign;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_pending == null || _pendingOffset >= _pending.Length)
            {
                if (!_reader.HasNextPacket) break;
                _pending = _reader.DecodeNextPacket();
                _pendingOffset = 0;
                if (_pending == null || _pending.Length == 0) continue;
            }
            int samplesWanted = (count - written) / 2;
            if (samplesWanted == 0) break;
            int n = Math.Min(samplesWanted, _pending.Length - _pendingOffset);
            Buffer.BlockCopy(_pending, _pendingOffset * 2, buffer, offset + written, n * 2);
            _pendingOffset += n;
            written += n * 2;
        }
        _position += written;
        return written;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}
