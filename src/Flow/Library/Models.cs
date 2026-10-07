using System.IO;
using System.Windows.Media;
using Flow.Infrastructure;

namespace Flow.Library;

public static class AudioFormats
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".mp4", ".aac", ".alac", ".wav", ".wma",
        ".flac", ".ogg", ".oga", ".opus", ".aif", ".aiff", ".aifc"
    };

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    public static string DialogFilter =>
        "Audio files|" + string.Join(";", Extensions.Select(e => "*" + e)) + "|All files|*.*";
}

public sealed class Track : ObservableObject
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public int Year { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public TimeSpan Duration { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public string Format { get; set; } = "";
    public long FileSize { get; set; }
    public long Mtime { get; set; }
    public DateTime DateAdded { get; set; } = DateTime.Now;
    public double? RgTrackGain { get; set; }
    public double? RgTrackPeak { get; set; }
    public double? RgAlbumGain { get; set; }
    public double? RgAlbumPeak { get; set; }
    /// <summary>Album grouping key; also the key of the cached artwork file.</summary>
    public string ArtKey { get; set; } = "";

    /// <summary>Set for tracks imported from Spotify (Path then holds the same URI).</summary>
    public string? SpotifyUri { get; set; }
    /// <summary>spotify:album:… for Spotify tracks; playback is started inside the album context.</summary>
    public string? SpotifyAlbumUri { get; set; }
    public string? ArtUrl { get; set; }
    public bool IsSpotify => SpotifyUri != null;
    public string SourceText => IsSpotify ? "Spotify" : "Local";

    private int _playCount;
    public int PlayCount { get => _playCount; set => Set(ref _playCount, value); }

    private DateTime? _lastPlayed;
    public DateTime? LastPlayed { get => _lastPlayed; set => Set(ref _lastPlayed, value); }

    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }

    private bool _isPlayingNow;
    public bool IsPlayingNow { get => _isPlayingNow; set => Set(ref _isPlayingNow, value); }

    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown Artist" : Artist;

    /// <summary>Individual artist names ("A, B" or "A; B" split) for clickable artist links.</summary>
    public IReadOnlyList<string> ArtistList => SplitArtists(DisplayArtist);

    /// <summary>The album cover for this song (shared cache; bind with IsAsync=True).</summary>
    public ImageSource? Thumb => ArtworkCache.Shared?.LoadThumb(ArtKey);

    /// <summary>First genre (for the clickable genre cell).</summary>
    public string PrimaryGenre => Genre.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    public static string[] SplitArtists(string s) =>
        s.Split(new[] { ", ", "; ", ";" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool HasArtist(string name) =>
        ArtistList.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        string.Equals(AlbumArtist, name, StringComparison.OrdinalIgnoreCase);
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "Unknown Album" : Album;
    public string DisplayAlbumArtist => !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist : DisplayArtist;
    public string DurationText => TimeFormat.Format(Duration);
    public string YearText => Year > 0 ? Year.ToString() : "";
    public string DateAddedText => DateAdded.ToString("yyyy-MM-dd");
    public string SubtitleLine => string.IsNullOrWhiteSpace(Album) ? DisplayArtist : $"{DisplayArtist}  ·  {Album}";

    public void CopyMetadataFrom(Track o)
    {
        Title = o.Title; Artist = o.Artist; AlbumArtist = o.AlbumArtist; Album = o.Album; Genre = o.Genre;
        Year = o.Year; TrackNumber = o.TrackNumber; DiscNumber = o.DiscNumber; Duration = o.Duration;
        Bitrate = o.Bitrate; SampleRate = o.SampleRate; Format = o.Format; FileSize = o.FileSize; Mtime = o.Mtime;
        RgTrackGain = o.RgTrackGain; RgTrackPeak = o.RgTrackPeak; RgAlbumGain = o.RgAlbumGain; RgAlbumPeak = o.RgAlbumPeak;
        ArtKey = o.ArtKey; SpotifyUri = o.SpotifyUri; SpotifyAlbumUri = o.SpotifyAlbumUri; ArtUrl = o.ArtUrl;
        OnPropertyChanged(string.Empty);
    }
}

public sealed class AlbumInfo
{
    private readonly ArtworkCache _art;

    public AlbumInfo(string key, List<Track> tracks, ArtworkCache art)
    {
        _art = art;
        Key = key;
        Tracks = tracks
            .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber == 0 ? int.MaxValue : t.TrackNumber)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var first = Tracks[0];
        Title = first.DisplayAlbum;
        var albumArtist = Tracks.Select(t => t.AlbumArtist).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (albumArtist != null) Artist = albumArtist;
        else
        {
            var artists = Tracks.Select(t => t.DisplayArtist).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Artist = artists.Count == 1 ? artists[0] : "Various Artists";
        }
        Year = Tracks.Select(t => t.Year).Where(y => y > 0).DefaultIfEmpty(0).Max();
        Genre = Tracks.Select(t => t.Genre).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)) ?? "";
        DateAdded = Tracks.Max(t => t.DateAdded);
        TotalDuration = TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks));
        PlayCount = Tracks.Sum(t => t.PlayCount);
        IsSpotify = Tracks.All(t => t.IsSpotify);
    }

    public bool IsSpotify { get; }

    public string Key { get; }
    public string Title { get; }
    public string Artist { get; }
    public int Year { get; }
    public string Genre { get; }
    public DateTime DateAdded { get; }
    public TimeSpan TotalDuration { get; }
    public int PlayCount { get; }
    public List<Track> Tracks { get; }

    public string InfoLine
    {
        get
        {
            var parts = new List<string>();
            if (Year > 0) parts.Add(Year.ToString());
            parts.Add(Tracks.Count == 1 ? "1 track" : $"{Tracks.Count} tracks");
            parts.Add(TimeFormat.Long(TotalDuration));
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Lazily-loaded cover; bind with IsAsync=True. Shared via the artwork cache's small LRU.</summary>
    public ImageSource? Thumb => _art.LoadThumb(Key);

    /// <summary>Album-detail artwork (shown at 260 px; decoded with headroom for high-DPI screens).</summary>
    public ImageSource? LargeArt => _art.LoadLarge(Key, 560);
}

/// <summary>Everything in the library by one artist: their albums, albums they appear on, and all songs.</summary>
public sealed class ArtistInfo
{
    public ArtistInfo(string name, List<AlbumInfo> albums, List<AlbumInfo> appearsOn, List<Track> tracks)
    {
        Name = name;
        Albums = albums;
        AppearsOn = appearsOn;
        Tracks = tracks;
    }

    public string Name { get; }
    public List<AlbumInfo> Albums { get; }
    public List<AlbumInfo> AppearsOn { get; }
    public List<Track> Tracks { get; }
    public bool HasAlbums => Albums.Count > 0;
    public bool HasAppearsOn => AppearsOn.Count > 0;
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public string InfoLine
    {
        get
        {
            var parts = new List<string>();
            if (Albums.Count > 0) parts.Add(Albums.Count == 1 ? "1 album" : $"{Albums.Count} albums");
            if (AppearsOn.Count > 0) parts.Add($"appears on {AppearsOn.Count}");
            parts.Add(Tracks.Count == 1 ? "1 song" : $"{Tracks.Count:N0} songs");
            parts.Add(TimeFormat.Long(TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks))));
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Cover of the newest album, used as the artist's picture.</summary>
    public ImageSource? Thumb => (Albums.FirstOrDefault() ?? AppearsOn.FirstOrDefault())?.Thumb;
}

public sealed class AlbumRow
{
    public AlbumRow(List<AlbumInfo> items) => Items = items;
    public List<AlbumInfo> Items { get; }
}

public sealed class PlaylistInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}
