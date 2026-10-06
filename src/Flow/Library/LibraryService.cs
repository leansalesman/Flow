using System.IO;
using Flow.Services;

namespace Flow.Library;

/// <summary>
/// The music library: scans configured folders, reads tags, extracts art, persists to SQLite and
/// watches folders for changes. <see cref="Changed"/> is raised on a background thread.
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LibraryDb _db;
    private readonly Dictionary<string, Track> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Threading.Timer _debounce;

    public ArtworkCache Art { get; }
    public event Action? Changed;
    public event Action<string>? StatusChanged;

    public string Status { get; private set; } = "";
    public bool IsScanning { get; private set; }

    public LibraryService(SettingsService settings, string dataDir)
    {
        _settings = settings;
        _db = new LibraryDb(dataDir);
        Art = new ArtworkCache(dataDir);
        _debounce = new System.Threading.Timer(_ => _ = ScanAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public LibraryDb Db => _db;

    public void LoadFromDatabase()
    {
        var tracks = _db.LoadTracks();
        var stats = _db.LoadStats();
        lock (_lock)
        {
            _byPath.Clear();
            foreach (var t in tracks)
            {
                if (stats.TryGetValue(t.Path, out var s))
                {
                    t.PlayCount = s.PlayCount; t.LastPlayed = s.LastPlayed; t.IsFavorite = s.Favorite;
                }
                _byPath[t.Path] = t;
            }
        }
        Changed?.Invoke();
    }

    public List<Track> Snapshot()
    {
        lock (_lock) return _byPath.Values.Concat(_spotify.Values).ToList();
    }

    public int Count { get { lock (_lock) return _byPath.Count + _spotify.Count; } }

    public Track? Find(string path)
    {
        lock (_lock)
            return _byPath.TryGetValue(path, out var t) ? t : _spotify.TryGetValue(path, out var s) ? s : null;
    }

    // ---- Spotify tracks (kept separate from the folder scan) ---------------------------

    private readonly Dictionary<string, Track> _spotify = new(StringComparer.Ordinal);

    /// <summary>Replaces the imported Spotify tracks, keeping existing instances so queues stay valid.</summary>
    public void SetSpotifyTracks(IEnumerable<Track> tracks)
    {
        var stats = _db.LoadStats();
        lock (_lock)
        {
            var next = new Dictionary<string, Track>(StringComparer.Ordinal);
            foreach (var t in tracks)
            {
                if (next.ContainsKey(t.Path)) continue;
                if (_spotify.TryGetValue(t.Path, out var existing))
                {
                    var added = existing.DateAdded;
                    existing.CopyMetadataFrom(t);
                    existing.DateAdded = t.DateAdded < added ? t.DateAdded : added;
                    next[t.Path] = existing;
                }
                else
                {
                    if (stats.TryGetValue(t.Path, out var s))
                    {
                        t.PlayCount = s.PlayCount; t.LastPlayed = s.LastPlayed; t.IsFavorite = s.Favorite;
                    }
                    next[t.Path] = t;
                }
            }
            _spotify.Clear();
            foreach (var kv in next) _spotify[kv.Key] = kv.Value;
        }
        Changed?.Invoke();
    }

    public int SpotifyCount { get { lock (_lock) return _spotify.Count; } }

    /// <summary>Returns the library track for a path, or reads tags for a file outside the library.</summary>
    public Track? GetOrRead(string path)
    {
        var t = Find(path);
        if (t != null) return t;
        if (!File.Exists(path)) return null;
        var track = ReadTrack(path, new FileInfo(path), extractArt: true);
        var stats = _db.LoadStats();
        if (stats.TryGetValue(path, out var s))
        {
            track.PlayCount = s.PlayCount; track.LastPlayed = s.LastPlayed; track.IsFavorite = s.Favorite;
        }
        return track;
    }

    public void SaveStats(Track t) => Task.Run(() => { try { _db.SaveStats(t); } catch { } });

    // ---- Scanning ---------------------------------------------------------------------

    public void RequestRescan(int delayMs = 2000)
    {
        _debounce.Change(delayMs, Timeout.Infinite);
    }

    public async Task ScanAsync()
    {
        if (!await _scanGate.WaitAsync(0))
        {
            // A scan is running; make sure another one follows it.
            RequestRescan(3000);
            return;
        }
        try
        {
            IsScanning = true;
            await Task.Run(ScanCore);
            Infrastructure.DiagLog.Write($"Scan complete: {Count} tracks");
        }
        catch (Exception ex)
        {
            Infrastructure.DiagLog.Write("Scan failed: " + ex);
            App.Log(ex);
        }
        finally
        {
            IsScanning = false;
            _scanGate.Release();
            SetStatus("");
        }
    }

    private void SetStatus(string s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }

    private void ScanCore()
    {
        var folders = _settings.Current.MusicFolders.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<Track>();
        var stats = _db.LoadStats();
        int processed = 0, changed = 0;
        Art.ResetMissing();

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*", opts); }
            catch { continue; }

            foreach (var file in files)
            {
                if (!AudioFormats.IsSupported(file)) continue;
                seen.Add(file);
                processed++;
                FileInfo fi;
                try { fi = new FileInfo(file); } catch { continue; }

                Track? existing;
                lock (_lock) _byPath.TryGetValue(file, out existing);
                long mtime = fi.LastWriteTimeUtc.Ticks;
                if (existing != null && existing.Mtime == mtime && existing.FileSize == fi.Length)
                {
                    if (!string.IsNullOrEmpty(existing.ArtKey) && !Art.Has(existing.ArtKey))
                        Art.Ensure(existing.ArtKey, existing.Path, null);
                    continue;
                }

                var t = ReadTrack(file, fi, extractArt: true);
                if (existing != null)
                {
                    t.Id = existing.Id;
                    t.DateAdded = existing.DateAdded;
                    t.PlayCount = existing.PlayCount;
                    t.LastPlayed = existing.LastPlayed;
                    t.IsFavorite = existing.IsFavorite;
                }
                else if (stats.TryGetValue(file, out var s))
                {
                    t.PlayCount = s.PlayCount; t.LastPlayed = s.LastPlayed; t.IsFavorite = s.Favorite;
                }
                batch.Add(t);
                changed++;
                if (changed % 20 == 0) SetStatus($"Scanning library… {changed} new or updated");
                if (batch.Count >= 250) Commit(batch);
            }
        }
        Commit(batch);

        // Remove tracks that vanished, or whose folder is no longer in the library.
        List<string> removed;
        lock (_lock)
        {
            removed = _byPath.Keys.Where(p =>
            {
                if (seen.Contains(p)) return false;
                var owner = folders.FirstOrDefault(f => IsUnder(p, f));
                if (owner == null) return true;              // folder removed from settings
                return Directory.Exists(owner);              // folder present but file gone (keep if drive offline)
            }).ToList();
            foreach (var p in removed) _byPath.Remove(p);
        }
        if (removed.Count > 0)
        {
            _db.Delete(removed);
            Changed?.Invoke();
        }
        if (changed == 0 && removed.Count == 0 && processed >= 0) { /* nothing to report */ }
    }

    private void Commit(List<Track> batch)
    {
        if (batch.Count == 0) return;
        _db.Upsert(batch);
        lock (_lock)
        {
            foreach (var t in batch)
            {
                if (_byPath.TryGetValue(t.Path, out var existing)) existing.CopyMetadataFrom(t);
                else _byPath[t.Path] = t;
            }
        }
        batch.Clear();
        Changed?.Invoke();
    }

    private static bool IsUnder(string path, string folder)
    {
        var f = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    public Track ReadTrack(string path, FileInfo fi, bool extractArt)
    {
        var t = new Track
        {
            Path = path,
            Title = Path.GetFileNameWithoutExtension(path),
            Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            FileSize = fi.Exists ? fi.Length : 0,
            Mtime = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0,
            DateAdded = DateTime.Now,
        };

        TagLib.File? f = null;
        try
        {
            f = TagLib.File.Create(path);
            var tag = f.Tag;
            if (!string.IsNullOrWhiteSpace(tag.Title)) t.Title = tag.Title.Trim();
            t.Artist = (tag.JoinedPerformers ?? "").Trim();
            t.AlbumArtist = (tag.JoinedAlbumArtists ?? "").Trim();
            t.Album = (tag.Album ?? "").Trim();
            t.Genre = (tag.JoinedGenres ?? "").Trim();
            t.Year = (int)tag.Year;
            t.TrackNumber = (int)tag.Track;
            t.DiscNumber = (int)tag.Disc;
            t.RgTrackGain = Valid(tag.ReplayGainTrackGain);
            t.RgTrackPeak = Valid(tag.ReplayGainTrackPeak);
            t.RgAlbumGain = Valid(tag.ReplayGainAlbumGain);
            t.RgAlbumPeak = Valid(tag.ReplayGainAlbumPeak);
            var props = f.Properties;
            if (props != null)
            {
                t.Duration = props.Duration;
                t.Bitrate = props.AudioBitrate;
                t.SampleRate = props.AudioSampleRate;
            }
        }
        catch { /* untagged or unsupported by TagLib - fall back to file name */ }

        // Some formats (e.g. WAV RIFF INFO) only carry an album-artist field.
        if (string.IsNullOrWhiteSpace(t.Artist) && !string.IsNullOrWhiteSpace(t.AlbumArtist)) t.Artist = t.AlbumArtist;

        if (string.IsNullOrWhiteSpace(t.Artist) && string.IsNullOrWhiteSpace(t.Album))
        {
            // Common "Artist - Title" file naming.
            var name = Path.GetFileNameWithoutExtension(path);
            var dash = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && t.Title == name)
            {
                t.Artist = name[..dash].Trim();
                t.Title = name[(dash + 3)..].Trim();
            }
        }

        t.ArtKey = ArtworkCache.MakeKey(t.AlbumArtist, t.Album, Path.GetDirectoryName(path) ?? "");
        if (extractArt) Art.Ensure(t.ArtKey, path, f);
        f?.Dispose();
        return t;
    }

    private static double? Valid(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;

    // ---- Watching ---------------------------------------------------------------------

    public void RefreshWatchers()
    {
        foreach (var w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        _watchers.Clear();
        foreach (var folder in _settings.Current.MusicFolders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                FileSystemEventHandler h = (_, e) => OnFsEvent(e.FullPath);
                w.Created += h;
                w.Changed += h;
                w.Deleted += h;
                w.Renamed += (_, e) => OnFsEvent(e.FullPath);
                w.Error += (_, _) => RequestRescan();
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch { /* network drives etc. may not support watching */ }
        }
    }

    private void OnFsEvent(string path)
    {
        // Ignore unrelated files (but directory changes have no extension and must trigger a rescan).
        var ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && !AudioFormats.IsSupported(path)
            && !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) return;
        RequestRescan();
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _debounce.Dispose();
        _db.Dispose();
    }
}
