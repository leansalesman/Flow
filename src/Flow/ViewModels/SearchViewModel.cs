using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Flow.Infrastructure;
using Flow.Library;
using Flow.Services;
using Flow.Spotify;

namespace Flow.ViewModels;

/// <summary>
/// Search page: a big centered search bar that suggests artists, songs and albums from all of Spotify as you
/// type. Picking a suggestion (or pressing Enter) shows full results with the bar moved to the top; clearing the
/// search brings the bar back to the center. Songs play in Flow, albums open in the spotlight.
/// </summary>
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
        _debounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RunSuggestAsync(); };
        SubmitCommand = new RelayCommand(async () => await SubmitAsync());
        PickSuggestionCommand = new RelayCommand(async p => { if (p is SpotifySuggestion s) await PickAsync(s); });
        ClearCommand = new RelayCommand(() => Query = "");
        PlaySongCommand = new RelayCommand(p => { if (p is SpotifySearchSong s) PlaySong(s); });
        OpenAlbumCommand = new RelayCommand(async p => { if (p is SpotifySearchAlbum a) await OpenAlbumAsync(a); });
        _spotify.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SpotifyService.IsConnected)) OnPropertyChanged(nameof(IsConnected)); };
    }

    public ObservableCollection<SpotifySearchSong> Songs { get; } = new();
    public ObservableCollection<SpotifySearchAlbum> Albums { get; } = new();
    public ICommand PlaySongCommand { get; }
    public ICommand OpenAlbumCommand { get; }
    public ICommand OpenSongAlbumCommand { get; }
    public ICommand SubmitCommand { get; }
    public ICommand PickSuggestionCommand { get; }
    public ICommand ClearCommand { get; }

    /// <summary>The dropdown under the search bar.</summary>
    public ObservableCollection<SpotifySuggestion> Suggestions { get; } = new();

    private bool _showSuggestions;
    public bool ShowSuggestions { get => _showSuggestions; set => Set(ref _showSuggestions, value); }

    private int _selectedSuggestion = -1;
    /// <summary>The suggestion highlighted with the arrow keys (-1 = none).</summary>
    public int SelectedSuggestion { get => _selectedSuggestion; set => Set(ref _selectedSuggestion, value); }

    private bool _isResultsMode;
    /// <summary>Results are showing: the search bar sits at the top. False = the bar is centered (nothing searched).</summary>
    public bool IsResultsMode { get => _isResultsMode; private set => Set(ref _isResultsMode, value); }

    /// <summary>The query the results show (an artist pick searches "artist:…" but shows the name).</summary>
    private string _resultsFor = "";

    public bool IsConnected => _spotify.IsConnected;

    private string _query = "";
    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value ?? "")) return;
            if (_suppressSuggest) return;
            _debounce.Stop();
            if (_query.Trim().Length == 0)
            {
                // Cleared: back to the centered bar.
                ++_generation;
                Suggestions.Clear();
                ShowSuggestions = false;
                Songs.Clear(); Albums.Clear(); HasResults = false;
                IsResultsMode = false;
                Status = "";
                return;
            }
            _debounce.Start();
        }
    }

    private bool _suppressSuggest;

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private bool _hasResults;
    public bool HasResults { get => _hasResults; private set => Set(ref _hasResults, value); }

    private int _suggestGeneration;

    /// <summary>Fills the dropdown for what's typed so far.</summary>
    private async Task RunSuggestAsync()
    {
        int gen = ++_suggestGeneration;
        var q = _query.Trim();
        if (q.Length < 1 || !_spotify.IsConnected)
        {
            Suggestions.Clear();
            ShowSuggestions = false;
            if (!_spotify.IsConnected) Status = "Connect Spotify in Settings to search";
            return;
        }
        List<SpotifySuggestion>? list;
        try { list = await _spotify.SuggestAsync(q); }
        catch { list = null; }
        if (gen != _suggestGeneration || _query.Trim() != q) return;   // the user kept typing
        Suggestions.Clear();
        foreach (var s in list ?? new()) Suggestions.Add(s);
        SelectedSuggestion = -1;
        ShowSuggestions = Suggestions.Count > 0;
    }

    /// <summary>Enter: full results for what's typed.</summary>
    public async Task SubmitAsync()
    {
        _debounce.Stop();
        ++_suggestGeneration;
        ShowSuggestions = false;
        var q = _query.Trim();
        if (q.Length == 0) return;
        await RunSearchAsync(q, q);
    }

    /// <summary>
    /// A suggestion was picked: an artist shows their songs and albums, a song plays (with results for it), an
    /// album opens in the spotlight (with results for it).
    /// </summary>
    public async Task PickAsync(SpotifySuggestion s)
    {
        _debounce.Stop();
        ++_suggestGeneration;
        ShowSuggestions = false;
        _suppressSuggest = true;
        Query = s.IsArtist ? s.Title : s.Kind == "Song" ? $"{s.Title} {s.Subtitle}" : $"{s.Title} {s.Subtitle}";
        _suppressSuggest = false;
        switch (s.Payload)
        {
            case string artist:
                await RunSearchAsync($"artist:\"{artist}\"", artist);
                break;
            case SpotifySearchSong song:
                await RunSearchAsync(_query, _query);
                var inResults = Songs.FirstOrDefault(x => x.Track.Path == song.Track.Path);
                if (inResults != null) PlaySong(inResults);
                else _pb.PlayTracks(new List<Track> { song.Track }, 0);
                break;
            case SpotifySearchAlbum album:
                await RunSearchAsync(_query, _query);
                await OpenAlbumAsync(album);
                break;
        }
    }

    private async Task RunSearchAsync(string query, string shownAs)
    {
        int gen = ++_generation;
        var q = query.Trim();
        if (q.Length == 0) return;
        _resultsFor = shownAs;
        IsResultsMode = true;
        if (!_spotify.IsConnected) { Status = "Connect Spotify in Settings to search"; return; }
        Status = "Searching…";
        SpotifySearchResults? r;
        try { r = await _spotify.SearchAsync(q); }
        catch { r = null; }
        if (gen != _generation) return;   // a newer search is under way
        Songs.Clear();
        Albums.Clear();
        if (r == null) { HasResults = false; Status = "Spotify search isn't available right now. Try again in a moment."; return; }
        foreach (var s in r.Songs) Songs.Add(s);
        foreach (var a in r.Albums) Albums.Add(a);
        HasResults = Songs.Count + Albums.Count > 0;
        Status = HasResults ? "" : $"No results for \"{_resultsFor}\"";
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
