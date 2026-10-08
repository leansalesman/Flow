using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flow.Library;

/// <summary>
/// Extracts album art (embedded or folder images) into %LOCALAPPDATA%\Flow\art as
/// {key}.jpg (max 800px) and {key}_t.jpg (300px thumbnail).
/// </summary>
public sealed class ArtworkCache
{
    private static readonly string[] FolderImageNames =
        { "cover", "folder", "front", "album", "albumart", "albumartsmall", "artwork" };

    private readonly string _dir;
    private readonly HashSet<string> _missing = new();
    private readonly object _lock = new();

    /// <summary>The app's artwork cache, so individual tracks can show their album cover (Track.Thumb).</summary>
    public static ArtworkCache? Shared { get; private set; }

    public ArtworkCache(string dataDir)
    {
        _dir = Path.Combine(dataDir, "art");
        Directory.CreateDirectory(_dir);
        Shared ??= this;
    }

    public static string MakeKey(string albumArtist, string album, string directory)
    {
        string basis = !string.IsNullOrWhiteSpace(album)
            ? (!string.IsNullOrWhiteSpace(albumArtist)
                ? "aa:" + albumArtist.Trim() + "|" + album.Trim()
                : "al:" + album.Trim() + "|" + directory)
            : "dir:" + directory;
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(basis.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 10).ToLowerInvariant();
    }

    public string LargePath(string key) => Path.Combine(_dir, key + ".jpg");
    public string ThumbPath(string key) => Path.Combine(_dir, key + "_t.jpg");
    public bool Has(string key) => !string.IsNullOrEmpty(key) && File.Exists(ThumbPath(key));
    /// <summary>The biggest cached file for a cover: the large one, or the thumbnail when the source was small.</summary>
    public string BestPath(string key) => File.Exists(LargePath(key)) ? LargePath(key) : ThumbPath(key);

    /// <summary>Ensures art for the key exists. Safe to call from any thread.</summary>
    public void Ensure(string key, string trackPath, TagLib.File? tagFile)
    {
        if (string.IsNullOrEmpty(key) || Has(key)) return;
        lock (_lock) { if (_missing.Contains(key)) return; }

        byte[]? data = null;
        try
        {
            var pics = tagFile?.Tag.Pictures;
            if (pics != null && pics.Length > 0)
            {
                var pic = pics.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pics[0];
                data = pic.Data?.Data;
            }
        }
        catch { /* ignore broken tags */ }

        if (data == null || data.Length == 0) data = FindFolderImage(Path.GetDirectoryName(trackPath));

        if (data == null || data.Length == 0 || !TrySave(key, data))
            lock (_lock) _missing.Add(key);
    }

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Downloads remote artwork (e.g. Spotify album covers) into the cache.</summary>
    public async Task<bool> EnsureFromUrlAsync(string key, string? url)
    {
        if (string.IsNullOrEmpty(key) || Has(key)) return true;
        if (string.IsNullOrEmpty(url)) return false;
        try
        {
            var data = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            return await Task.Run(() => TrySave(key, data)).ConfigureAwait(false);
        }
        catch { return false; }
    }

    /// <summary>
    /// Makes sure the large cover exists, downloading it from <paramref name="url"/> when only the thumbnail is
    /// cached (Spotify covers are synced at 300 px; the 640 px one is fetched when a big view needs it).
    /// </summary>
    private readonly HashSet<string> _largeTried = new();

    public async Task EnsureLargeFromUrlAsync(string key, string? url)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(url) || File.Exists(LargePath(key))) return;
        lock (_lock) if (!_largeTried.Add(key)) return;   // the source may simply be small: try once per run
        try
        {
            var data = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            await Task.Run(() => TrySave(key, data)).ConfigureAwait(false);
        }
        catch { }
    }

    public void ResetMissing()
    {
        lock (_lock) _missing.Clear();
    }

    private static byte[]? FindFolderImage(string? dir)
    {
        if (dir == null || !Directory.Exists(dir)) return null;
        try
        {
            var files = Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var match = files.FirstOrDefault(f =>
                FolderImageNames.Contains(Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase))
                ?? (files.Count == 1 ? files[0] : null);
            return match != null ? File.ReadAllBytes(match) : null;
        }
        catch { return null; }
    }

    private bool TrySave(string key, byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapSource src = decoder.Frames[0];
            if (src.PixelWidth < 8 || src.PixelHeight < 8) return false;
            if (src.Format != PixelFormats.Bgr32 && src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32)
                src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            // No large copy of a small source: it would only be the thumbnail again (LoadLarge falls back to it).
            if (Math.Max(src.PixelWidth, src.PixelHeight) > 300) SaveScaled(src, 800, LargePath(key));
            if (!File.Exists(ThumbPath(key)) || Math.Max(src.PixelWidth, src.PixelHeight) > 300) SaveScaled(src, 300, ThumbPath(key));
            return true;
        }
        catch { return false; }
    }

    private static void SaveScaled(BitmapSource src, int max, string path)
    {
        double scale = Math.Min(1.0, (double)max / Math.Max(src.PixelWidth, src.PixelHeight));
        BitmapSource img = scale < 1.0 ? new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src;
        var enc = new JpegBitmapEncoder { QualityLevel = 90 };
        enc.Frames.Add(BitmapFrame.Create(img));
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, path, true);
    }

    // ---- In-memory covers ----
    // Shelf covers are shared across library rebuilds (sort, filter, sync) through a small LRU cache instead
    // of being decoded again each time, and are decoded at the size the shelves actually draw them.

    private const int ThumbCacheCapacity = 140; // Small tiles show ~60 covers at once; keep a screen or two of headroom
    private readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> _thumbIndex = new();
    private readonly LinkedList<(string Key, ImageSource Image)> _thumbLru = new();
    private readonly object _thumbLock = new();
    private int _thumbPx = 256;

    /// <summary>Pixel width covers are decoded at (tile size × display scale). Changing it notably resets the cache.</summary>
    public int ThumbDecodePx
    {
        get => _thumbPx;
        set
        {
            value = Math.Clamp((value + 31) / 32 * 32, 96, 320);
            if (Math.Abs(value - _thumbPx) < _thumbPx / 5) return;
            _thumbPx = value;
            lock (_thumbLock) { _thumbIndex.Clear(); _thumbLru.Clear(); }
        }
    }

    public ImageSource? LoadThumb(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (_thumbLock)
        {
            if (_thumbIndex.TryGetValue(key, out var node))
            {
                _thumbLru.Remove(node);
                _thumbLru.AddFirst(node);
                return node.Value.Image;
            }
        }
        var img = Load(ThumbPath(key), _thumbPx);
        if (img == null) return null;
        lock (_thumbLock)
        {
            if (_thumbIndex.TryGetValue(key, out var existing)) return existing.Value.Image;
            _thumbIndex[key] = _thumbLru.AddFirst((key, img));
            while (_thumbLru.Count > ThumbCacheCapacity)
            {
                var last = _thumbLru.Last!;
                _thumbLru.RemoveLast();
                _thumbIndex.Remove(last.Value.Key);
            }
        }
        return img;
    }

    /// <summary>Large artwork, decoded no bigger than needed (Now Playing / album detail).</summary>
    public ImageSource? LoadLarge(string key, int maxPx = 720) =>
        string.IsNullOrEmpty(key) ? null : Load(BestPath(key), maxPx);

    private static ImageSource? Load(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // Load from a stream (not a URI) so WPF's global image cache doesn't keep extra copies around.
            // (Don't add IgnoreImageCache here: with a StreamSource WPF throws "Key cannot be null".)
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = fs;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            // Never fail silently again: a broken loader would otherwise just look like "no artwork".
            Flow.Spotify.SpotifyLog.Write($"Artwork load failed for {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }
}
