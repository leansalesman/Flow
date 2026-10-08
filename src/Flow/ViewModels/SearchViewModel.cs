using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Flow.Infrastructure;
using Flow.Library;
using Flow.Services;
using Flow.Spotify;

namespace Flow.ViewModels;

/// <summary>Search page: searches all of Spotify as you type; songs play in Flow, albums open in the spotlight.</summary>
public sealed class SearchViewModel : ObservableObject
{
    private readonly SpotifyService _spotify;
    private readonly PlaybackService _pb;
    private readonly LibraryService _lib;
    private readonly Action<AlbumInfo> _openAlbum;
    private readonly Action<string> _toast;
    private readonly DispatcherTimer _debounce;
    private int _generation;

    public SearchViewModel(SpotifyService spotify, PlaybackService pb, LibraryService lib, Action<AlbumInfo> openAlbum,
                           Action<string> toast, Dispatcher ui)
    {
        _toast = toast;
        AddSongCommand = new RelayCommand(async p => { if (p is SpotifySearchSong s) await AddSongAsync(s); });
        OpenSongAlbumCommand = new RelayCommand(async p =>
        {
            // Title, artist and album links on a song open its album in the spotlight.
            if (p is SpotifySearchSong { Track.SpotifyAlbumUri: { } albumUri } s)
                await OpenAlbumAsync(new SpotifySearchAlbum(albumUri, s.Track.Album, s.Track.AlbumArtist, s.Track.Year, 0,
                                                            s.ImageUrl, s.Track.ArtUrl));
        });
        _spotify = spotify;
        _pb = pb;
        _lib = lib;
        _openAlbum = openAlbum;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RunSearchAsync(); };
        PlaySongCommand = new RelayCommand(p => { if (p is SpotifySearchSong s) PlaySong(s); });
        OpenAlbumCommand = new RelayCommand(async p => { if (p is SpotifySearchAlbum a) await OpenAlbumAsync(a); });
        _spotify.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SpotifyService.IsConnected)) OnPropertyChanged(nameof(IsConnected)); };
    }

    public ObservableCollection<SpotifySearchSong> Songs { get; } = new();
    public ObservableCollection<SpotifySearchAlbum> Albums { get; } = new();
    public ICommand PlaySongCommand { get; }
    public ICommand OpenAlbumCommand { get; }
    public ICommand AddSongCommand { get; }
    public ICommand OpenSongAlbumCommand { get; }

    public bool IsConnected => _spotify.IsConnected;

    private string _query = "";
    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value ?? "")) return;
            _debounce.Stop();
            _debounce.Start();
        }
    }

    private string _status = "Search for songs and albums on Spotify";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private bool _hasResults;
    public bool HasResults { get => _hasResults; private set => Set(ref _hasResults, value); }

    private async Task RunSearchAsync()
    {
        int gen = ++_generation;
        var q = _query.Trim();
        if (q.Length < 2)
        {
            Songs.Clear(); Albums.Clear(); HasResults = false;
            Status = "Search for songs and albums on Spotify";
            return;
        }
        if (!_spotify.IsConnected) { Status = "Connect Spotify in Settings to search"; return; }
        Status = "Searching…";
        SpotifySearchResults? r;
        try { r = await _spotify.SearchAsync(q); }
        catch { r = null; }
        if (gen != _generation) return;   // a newer search is under way
        Songs.Clear();
        Albums.Clear();
        if (r == null) { HasResults = false; Status = "Spotify search isn't available right now. Try again in a moment."; return; }
        var owned = OwnedLookup();
        foreach (var s in r.Songs) Songs.Add(owned.ContainsKey(s.Track.Path) ? s with { InLibrary = true } : s);
        foreach (var a in r.Albums) Albums.Add(a);
        HasResults = Songs.Count + Albums.Count > 0;
        Status = HasResults ? "" : $"No results for \"{q}\"";
    }

    /// <summary>"+": saves the song to Spotify Liked Songs and adds it to Flow's library.</summary>
    private async Task AddSongAsync(SpotifySearchSong song)
    {
        if (song.InLibrary) return;
        var err = await _spotify.AddToLibraryAsync(song.Track);
        if (err != null) { _toast(err); return; }
        int i = Songs.IndexOf(song);
        if (i >= 0) Songs[i] = song with { InLibrary = true };
        _toast($"Added \"{song.Track.Title}\" to your library and Spotify Liked Songs");
    }

    /// <summary>Plays the song; the other songs in the results follow it in the queue.</summary>
    private void PlaySong(SpotifySearchSong song)
    {
        var owned = OwnedLookup();
        var tracks = Songs.Select(s => owned.GetValueOrDefault(s.Track.Path) ?? s.Track).ToList();
        int index = Songs.IndexOf(song);
        if (index >= 0) _pb.PlayTracks(tracks, index);
    }

    /// <summary>Opens the album's full tracklist in the Library's album spotlight.</summary>
    private async Task OpenAlbumAsync(SpotifySearchAlbum album)
    {
        Status = $"Opening {album.Name}…";
        var tracks = await _spotify.GetAlbumTracksAsync(album.Uri);
        if (tracks.Count == 0) { Status = "Couldn't load that album from Spotify."; return; }
        await _lib.Art.EnsureFromUrlAsync(tracks[0].ArtKey, album.LargeImageUrl ?? album.ImageUrl);
        Status = "";
        var owned = OwnedLookup();
        var merged = tracks.Select(t => owned.GetValueOrDefault(t.Path) ?? t).ToList();
        _openAlbum(new AlbumInfo(tracks[0].ArtKey, merged, _lib.Art) { IsFullAlbum = true });
    }

    /// <summary>The library's own copies of songs by path (keeps favorites and play counts).</summary>
    private Dictionary<string, Track> OwnedLookup() =>
        _lib.Snapshot().GroupBy(t => t.Path).ToDictionary(g => g.Key, g => g.First());
}
