using System.Windows.Input;
using System.Windows.Threading;
using Flow.Infrastructure;
using Flow.Library;
using Flow.Services;

namespace Flow.ViewModels;

public enum SortField { TrackName, ArtistName, AlbumName, TrackLength, Year, Genre, PlayCount, DateAdded }

public sealed class LibraryViewModel : ObservableObject
{
    private readonly LibraryService _lib;
    private readonly PlaybackService _pb;
    private readonly SettingsService _settings;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _rebuildTimer;
    private int _rebuildToken;

    public static readonly IReadOnlyList<(SortField Field, string Label)> SortOptions = new[]
    {
        (SortField.TrackName, "Track Name"),
        (SortField.ArtistName, "Artist Name"),
        (SortField.AlbumName, "Album Name"),
        (SortField.TrackLength, "Track Length"),
        (SortField.Year, "Year"),
        (SortField.Genre, "Genre"),
        (SortField.PlayCount, "Most Played"),
        (SortField.DateAdded, "Date Added"),
    };

    private readonly Flow.Spotify.SpotifyService? _spotify;

    public LibraryViewModel(LibraryService lib, PlaybackService pb, SettingsService settings, Dispatcher ui,
                            Flow.Spotify.SpotifyService? spotify = null)
    {
        _spotify = spotify;
        ShowFullAlbumCommand = new RelayCommand(async () => await ShowFullAlbumAsync(), () => CanShowFullAlbum && !_loadingFullAlbum);
        _lib = lib;
        _pb = pb;
        _settings = settings;
        _ui = ui;
        _isGridView = settings.Current.LibraryView != "Table";
        _sortField = Enum.TryParse<SortField>(settings.Current.SortField, out var sf) ? sf : SortField.ArtistName;
        _sortDescending = settings.Current.SortDescending;
        _source = settings.Current.LibrarySource is "Local" or "Spotify" ? settings.Current.LibrarySource : "All";
        _artSize = settings.Current.LibraryArtSize is "Small" or "Large" ? settings.Current.LibraryArtSize : "Medium";
        _textSize = settings.Current.SpotlightTextSize is "Small" or "Large" ? settings.Current.SpotlightTextSize : "Medium";

        _rebuildTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(150) };
        _rebuildTimer.Tick += (_, _) => { _rebuildTimer.Stop(); Rebuild(); };

        _lib.Changed += () => _ui.BeginInvoke(() => ScheduleRebuild(600));
        _lib.StatusChanged += s => _ui.BeginInvoke(() => ScanStatus = s);

        SetSortCommand = new RelayCommand(p =>
        {
            if (p is SortField f) SetSort(f);
            else if (p is string s && Enum.TryParse<SortField>(s, out var f2)) SetSort(f2);
        });
        ToggleSortDirectionCommand = new RelayCommand(() => SortDescending = !SortDescending);
        ShowGridCommand = new RelayCommand(() => IsGridView = true);
        ShowTableCommand = new RelayCommand(() => IsGridView = false);
        InitLinkCommands();
        OpenAlbumCommand = new RelayCommand(p => SelectedAlbum = p as AlbumInfo);
        CloseAlbumCommand = new RelayCommand(() => SelectedAlbum = null);
        PlayAlbumCommand = new RelayCommand(p => { if (p is AlbumInfo a) _pb.PlayTracks(a.Tracks, 0); });
        ShuffleAlbumCommand = new RelayCommand(p => { if (p is AlbumInfo a) _pb.PlayTracks(a.Tracks, 0, shuffle: true); });
        PlayAllCommand = new RelayCommand(() => { if (Tracks.Count > 0) _pb.PlayTracks(Tracks, 0); });
        ShuffleAllCommand = new RelayCommand(() => { if (Tracks.Count > 0) _pb.PlayTracks(Tracks, 0, shuffle: true); });
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ClearGenresCommand = new RelayCommand(ClearGenres);
    }

    public ICommand SetSortCommand { get; }
    public ICommand ToggleSortDirectionCommand { get; }
    public ICommand ShowGridCommand { get; }
    public ICommand ShowTableCommand { get; }
    public ICommand OpenAlbumCommand { get; }
    public ICommand CloseAlbumCommand { get; }
    public ICommand PlayAlbumCommand { get; }
    public ICommand ShuffleAlbumCommand { get; }
    public ICommand PlayAllCommand { get; }
    public ICommand ShuffleAllCommand { get; }
    public ICommand ClearSearchCommand { get; }

    private string _source = "All";
    /// <summary>"All", "Local" or "Spotify".</summary>
    public string Source
    {
        get => _source;
        set
        {
            if (!Set(ref _source, value ?? "All")) return;
            _settings.Current.LibrarySource = _source;
            Rebuild();
        }
    }

    private string _search = "";
    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) ScheduleRebuild(150); }
    }

    private bool _isGridView;
    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (!Set(ref _isGridView, value)) return;
            _settings.Current.LibraryView = value ? "Grid" : "Table";
        }
    }

    private SortField _sortField;
    public SortField SortField
    {
        get => _sortField;
        private set
        {
            if (!Set(ref _sortField, value)) return;
            _settings.Current.SortField = value.ToString();
            OnPropertyChanged(nameof(SortLabel));
        }
    }

    private bool _sortDescending;
    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            if (!Set(ref _sortDescending, value)) return;
            _settings.Current.SortDescending = value;
            Rebuild();
        }
    }

    public string SortLabel => SortOptions.First(o => o.Field == _sortField).Label;

    public void SetSort(SortField f)
    {
        if (f == _sortField) { SortDescending = !SortDescending; return; }
        SortField = f;
        // Numeric/time sorts read best largest-first.
        _sortDescending = f is SortField.PlayCount or SortField.DateAdded;
        _settings.Current.SortDescending = _sortDescending;
        OnPropertyChanged(nameof(SortDescending));
        Rebuild();
    }

    private List<Track> _tracks = new();
    public List<Track> Tracks { get => _tracks; private set => Set(ref _tracks, value); }

    private List<AlbumInfo> _albums = new();
    public List<AlbumInfo> Albums { get => _albums; private set { Set(ref _albums, value); BuildRows(); } }

    private List<AlbumRow> _rows = new();
    public List<AlbumRow> Rows { get => _rows; private set => Set(ref _rows, value); }

    private int _columns = 5;
    public int ColumnsPerRow
    {
        get => _columns;
        set { if (Set(ref _columns, Math.Max(1, value))) BuildRows(); }
    }

    // ---- Artwork size (Small / Medium / Large) ----

    private string _artSize = "Medium";
    /// <summary>"Small", "Medium" or "Large": shelf tile size and the cover column in the track table.</summary>
    public string ArtSize
    {
        get => _artSize;
        set
        {
            value = value is "Small" or "Large" ? value : "Medium";
            if (!Set(ref _artSize, value)) return;
            _settings.Current.LibraryArtSize = value;
            OnPropertyChanged(nameof(MinTileWidth));
            OnPropertyChanged(nameof(TableThumb));
            OnPropertyChanged(nameof(TableRowHeight));
        }
    }

    // ---- Album spotlight / artist page text size ----

    private string _textSize = "Medium";
    /// <summary>"Small" (original size), "Medium" or "Large": scales the album spotlight and artist page.</summary>
    public string TextSize
    {
        get => _textSize;
        set
        {
            value = value is "Small" or "Large" ? value : "Medium";
            if (!Set(ref _textSize, value)) return;
            _settings.Current.SpotlightTextSize = value;
            OnPropertyChanged(nameof(TextScale));
        }
    }

    public double TextScale => _textSize switch { "Small" => 1.0, "Large" => 1.5, _ => 1.25 };

    /// <summary>Smallest shelf tile width; the shelves fit as many as possible and stretch them to fill.</summary>
    public double MinTileWidth => _artSize switch { "Small" => 120, "Large" => 236, _ => 168 };
    public double TableThumb => _artSize switch { "Small" => 26, "Large" => 52, _ => 36 };
    public double TableRowHeight => TableThumb + 14;

    private double _tileWidth = 180;
    public double TileWidth { get => _tileWidth; set => Set(ref _tileWidth, value); }

    private AlbumInfo? _selectedAlbum;
    public AlbumInfo? SelectedAlbum
    {
        get => _selectedAlbum;
        set
        {
            Set(ref _selectedAlbum, value);
            OnPropertyChanged(nameof(IsAlbumOpen));
            OnPropertyChanged(nameof(ShowArtistOverlay));
            OnPropertyChanged(nameof(CanShowFullAlbum));
        }
    }
    public bool IsAlbumOpen => _selectedAlbum != null;

    // ---- "Show full album": the whole Spotify tracklist, not just the songs in the library ----

    public ICommand ShowFullAlbumCommand { get; }
    private bool _loadingFullAlbum;

    public bool CanShowFullAlbum => _selectedAlbum is { IsFullAlbum: false } a && _spotify?.IsConnected == true
                                    && a.Tracks.Any(t => t.SpotifyAlbumUri != null);

    private async Task ShowFullAlbumAsync()
    {
        var album = _selectedAlbum;
        var uri = album?.Tracks.FirstOrDefault(t => t.SpotifyAlbumUri != null)?.SpotifyAlbumUri;
        if (album == null || uri == null || _spotify == null) return;
        _loadingFullAlbum = true;
        List<Track> full;
        try { full = await _spotify.GetAlbumTracksAsync(uri); }
        finally { _loadingFullAlbum = false; }
        if (!ReferenceEquals(_selectedAlbum, album) || full.Count == 0) return;

        // Songs already in the library keep their favorites and play counts; songs from other
        // copies of the album (e.g. local files) stay listed too.
        var owned = album.Tracks.GroupBy(t => t.Path).ToDictionary(g => g.Key, g => g.First());
        var merged = full.Select(t =>
        {
            if (owned.Remove(t.Path, out var mine)) return mine;
            if (string.IsNullOrEmpty(t.Genre)) t.Genre = album.Genre;
            return t;
        }).Concat(owned.Values).ToList();
        SelectedAlbum = new AlbumInfo(album.Key, merged, _lib.Art) { IsFullAlbum = true };
    }

    // ---- Artist pages & links ----

    private ArtistInfo? _selectedArtist;
    public ArtistInfo? SelectedArtist
    {
        get => _selectedArtist;
        set { Set(ref _selectedArtist, value); OnPropertyChanged(nameof(IsArtistOpen)); OnPropertyChanged(nameof(ShowArtistOverlay)); }
    }
    public bool IsArtistOpen => _selectedArtist != null;
    /// <summary>The artist page hides while an album opened from it is on top (Back returns to it).</summary>
    public bool ShowArtistOverlay => _selectedArtist != null && _selectedAlbum == null;

    public ICommand OpenArtistCommand { get; private set; } = null!;
    public ICommand CloseArtistCommand { get; private set; } = null!;
    public ICommand OpenAlbumOfTrackCommand { get; private set; } = null!;
    public ICommand FilterGenreCommand { get; private set; } = null!;
    public ICommand PlayArtistCommand { get; private set; } = null!;
    public ICommand ShuffleArtistCommand { get; private set; } = null!;

    private void InitLinkCommands()
    {
        OpenArtistCommand = new RelayCommand(p => { if (p is string name) OpenArtist(name); });
        CloseArtistCommand = new RelayCommand(() => SelectedArtist = null);
        OpenAlbumOfTrackCommand = new RelayCommand(p =>
        {
            if (p is Track t) OpenAlbumOf(t);
            else if (p is AlbumInfo a) SelectedAlbum = a;
        });
        FilterGenreCommand = new RelayCommand(p =>
        {
            var g = p is Track t ? t.PrimaryGenre : p as string;
            if (!string.IsNullOrEmpty(g)) FilterToGenre(g);
        });
        PlayArtistCommand = new RelayCommand(p => { if (p is ArtistInfo a) _pb.PlayTracks(a.Tracks, 0); });
        ShuffleArtistCommand = new RelayCommand(p => { if (p is ArtistInfo a) _pb.PlayTracks(a.Tracks, 0, shuffle: true); });
    }

    /// <summary>Opens the artist page: their albums, albums they appear on, and all their songs.</summary>
    public void OpenArtist(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "Unknown Artist" or "Various Artists") return;
        var all = _lib.Snapshot();
        var tracks = all.Where(t => t.HasArtist(name)).ToList();
        if (tracks.Count == 0) return;

        var keys = new HashSet<string>(tracks.Select(t => t.ArtKey));
        var albums = all.Where(t => keys.Contains(t.ArtKey)).GroupBy(t => t.ArtKey)
            .Select(g => new AlbumInfo(g.Key, g.ToList(), _lib.Art)).ToList();
        // Their own albums: credited to them, or where they're on most of the songs. The rest: "appears on".
        bool Own(AlbumInfo a) => string.Equals(a.Artist, name, StringComparison.OrdinalIgnoreCase)
                                 || a.Tracks.Count(t => t.HasArtist(name)) * 2 > a.Tracks.Count;
        static List<AlbumInfo> Newest(IEnumerable<AlbumInfo> x) =>
            x.OrderByDescending(a => a.Year).ThenBy(a => a.Title, Cmp).ToList();

        var own = Newest(albums.Where(Own));
        var appears = Newest(albums.Where(a => !Own(a)));
        var albumOrder = own.Concat(appears).Select((a, i) => (a.Key, i)).ToDictionary(x => x.Key, x => x.i);
        var songs = tracks
            .OrderBy(t => albumOrder.TryGetValue(t.ArtKey, out var i) ? i : int.MaxValue)
            .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title, Cmp)
            .ToList();

        SelectedAlbum = null;
        SelectedArtist = new ArtistInfo(name, own, appears, songs);
    }

    /// <summary>Spotlights the full album a track belongs to (regardless of current filters).</summary>
    public void OpenAlbumOf(Track t)
    {
        var tracks = _lib.Snapshot().Where(x => x.ArtKey == t.ArtKey).ToList();
        if (tracks.Count == 0) tracks.Add(t);
        SelectedAlbum = new AlbumInfo(t.ArtKey, tracks, _lib.Art);
    }

    /// <summary>Shows only this genre (same as selecting just its chip).</summary>
    public void FilterToGenre(string genre)
    {
        _selectedGenres.Clear();
        _selectedGenres.Add(genre);
        foreach (var c in GenreChips) c.SetSilently(string.Equals(c.Key, genre, StringComparison.OrdinalIgnoreCase));
        SelectedAlbum = null;
        SelectedArtist = null;
        OnPropertyChanged(nameof(HasGenreFilter));
        Rebuild();
    }

    public void PlayFromArtist(Track t)
    {
        var a = SelectedArtist;
        if (a == null) return;
        int i = a.Tracks.IndexOf(t);
        if (i >= 0) _pb.PlayTracks(a.Tracks, i);
    }

    private string _summary = "";
    public string SummaryText { get => _summary; private set => Set(ref _summary, value); }

    private string _scanStatus = "";
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }

    private bool _isLibraryEmpty = true;
    public bool IsLibraryEmpty { get => _isLibraryEmpty; private set => Set(ref _isLibraryEmpty, value); }

    public void ScheduleRebuild(int ms)
    {
        _rebuildTimer.Stop();
        _rebuildTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _rebuildTimer.Start();
    }

    public void Rebuild()
    {
        int token = ++_rebuildToken;
        var all = _lib.Snapshot();
        var query = _search.Trim();
        var field = _sortField;
        bool desc = _sortDescending;
        var art = _lib.Art;
        string? openAlbumKey = _selectedAlbum?.Key;
        var source = _source;
        var genres = new HashSet<string>(_selectedGenres, StringComparer.OrdinalIgnoreCase);

        Task.Run(() =>
        {
            IEnumerable<Track> filtered = source switch
            {
                "Local" => all.Where(t => !t.IsSpotify),
                "Spotify" => all.Where(t => t.IsSpotify),
                _ => all,
            };
            // Chip counts reflect the current source (All/Local/Spotify), before genre and search filters.
            var bySource = filtered.ToList();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in bySource)
                foreach (var g in GenresOf(t))
                    counts[g] = counts.TryGetValue(g, out var n) ? n + 1 : 1;

            filtered = bySource;
            if (genres.Count > 0)
                filtered = filtered.Where(t => GenresOf(t).Any(genres.Contains));
            if (query.Length > 0)
            {
                var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                filtered = filtered.Where(t => terms.All(term =>
                    t.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                    t.Artist.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                    t.AlbumArtist.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                    t.Album.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                    t.Genre.Contains(term, StringComparison.CurrentCultureIgnoreCase)));
            }
            var list = filtered.ToList();
            var tracks = SortTracks(list, field, desc);
            var albums = SortAlbums(list.GroupBy(t => t.ArtKey).Select(g => new AlbumInfo(g.Key, g.ToList(), art)).ToList(), field, desc);
            var summary = $"{albums.Count:N0} album{(albums.Count == 1 ? "" : "s")}  ·  {tracks.Count:N0} track{(tracks.Count == 1 ? "" : "s")}";
            if (genres.Count > 0) summary += "  ·  " + string.Join(" + ", genres.Select(g => g.Length == 0 ? NoGenre : g));
            return (tracks, albums, summary, empty: all.Count == 0, counts);
        }).ContinueWith(t =>
        {
            if (token != _rebuildToken || t.IsFaulted) return;
            SyncGenreChips(t.Result.counts);
            Tracks = t.Result.tracks;
            Albums = t.Result.albums;
            SummaryText = t.Result.summary;
            IsLibraryEmpty = t.Result.empty;
            if (openAlbumKey != null)
                SelectedAlbum = t.Result.albums.FirstOrDefault(a => a.Key == openAlbumKey) ?? _selectedAlbum;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static readonly StringComparer Cmp = StringComparer.CurrentCultureIgnoreCase;

    // ---- Genre filter ----

    public const string NoGenre = "No genre";
    private readonly HashSet<string> _selectedGenres = new(StringComparer.OrdinalIgnoreCase);

    public System.Collections.ObjectModel.ObservableCollection<GenreChip> GenreChips { get; } = new();
    public bool HasGenreFilter => _selectedGenres.Count > 0;
    public ICommand ClearGenresCommand { get; private set; } = null!;

    /// <summary>A track's genres (multi-genre tags like "Rock; Pop" are split); "" = no genre.</summary>
    private static IEnumerable<string> GenresOf(Track t)
    {
        if (string.IsNullOrWhiteSpace(t.Genre)) { yield return ""; yield break; }
        foreach (var g in t.Genre.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return g;
    }

    private void OnChipToggled(GenreChip chip)
    {
        if (chip.IsSelected) _selectedGenres.Add(chip.Key); else _selectedGenres.Remove(chip.Key);
        OnPropertyChanged(nameof(HasGenreFilter));
        Rebuild();
    }

    private void ClearGenres()
    {
        _selectedGenres.Clear();
        foreach (var c in GenreChips) c.SetSilently(false);
        OnPropertyChanged(nameof(HasGenreFilter));
        Rebuild();
    }

    /// <summary>Updates chips in place (keeps scroll position and selection), most common genres first.</summary>
    private void SyncGenreChips(Dictionary<string, int> counts)
    {
        var ordered = counts.Where(kv => kv.Key.Length > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, Cmp)
            .Concat(counts.Where(kv => kv.Key.Length == 0))   // "No genre" last
            .ToList();
        bool sameShape = ordered.Count == GenreChips.Count &&
                         ordered.Select(kv => kv.Key).SequenceEqual(GenreChips.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
        if (sameShape)
        {
            for (int i = 0; i < ordered.Count; i++) GenreChips[i].Count = ordered[i].Value;
            return;
        }
        GenreChips.Clear();
        foreach (var (key, count) in ordered)
            GenreChips.Add(new GenreChip(key, key.Length == 0 ? NoGenre : key, count, _selectedGenres.Contains(key), OnChipToggled));
        // Forget selections for genres that no longer exist in this source (e.g. after switching to Local).
        int removed = _selectedGenres.RemoveWhere(g => !counts.ContainsKey(g));
        if (removed > 0) { OnPropertyChanged(nameof(HasGenreFilter)); Rebuild(); }
    }

    private static List<Track> SortTracks(List<Track> list, SortField f, bool desc)
    {
        IOrderedEnumerable<Track> o = f switch
        {
            SortField.TrackName => Order(list, t => t.Title, desc),
            SortField.ArtistName => Order(list, t => t.DisplayAlbumArtist, desc).ThenBy(t => t.Year).ThenBy(t => t.Album, Cmp),
            SortField.AlbumName => Order(list, t => t.DisplayAlbum, desc),
            SortField.TrackLength => desc ? list.OrderByDescending(t => t.Duration) : list.OrderBy(t => t.Duration),
            SortField.Year => desc ? list.OrderByDescending(t => t.Year) : list.OrderBy(t => t.Year),
            SortField.Genre => desc
                ? list.OrderBy(t => string.IsNullOrEmpty(t.Genre)).ThenByDescending(t => t.Genre, Cmp)
                : list.OrderBy(t => string.IsNullOrEmpty(t.Genre)).ThenBy(t => t.Genre, Cmp),
            SortField.PlayCount => desc ? list.OrderByDescending(t => t.PlayCount) : list.OrderBy(t => t.PlayCount),
            SortField.DateAdded => desc ? list.OrderByDescending(t => t.DateAdded) : list.OrderBy(t => t.DateAdded),
            _ => list.OrderBy(t => t.Title, Cmp),
        };
        if (f is not SortField.TrackName)
            o = o.ThenBy(t => t.Album, Cmp).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title, Cmp);
        return o.ToList();
    }

    private static IOrderedEnumerable<Track> Order(List<Track> l, Func<Track, string> key, bool desc)
        => desc ? l.OrderByDescending(key, Cmp) : l.OrderBy(key, Cmp);

    private static List<AlbumInfo> SortAlbums(List<AlbumInfo> list, SortField f, bool desc)
    {
        IOrderedEnumerable<AlbumInfo> o = f switch
        {
            SortField.ArtistName => desc ? list.OrderByDescending(a => a.Artist, Cmp) : list.OrderBy(a => a.Artist, Cmp),
            SortField.TrackLength => desc ? list.OrderByDescending(a => a.TotalDuration) : list.OrderBy(a => a.TotalDuration),
            SortField.Year => desc ? list.OrderByDescending(a => a.Year) : list.OrderBy(a => a.Year),
            SortField.Genre => desc
                ? list.OrderBy(a => string.IsNullOrEmpty(a.Genre)).ThenByDescending(a => a.Genre, Cmp)
                : list.OrderBy(a => string.IsNullOrEmpty(a.Genre)).ThenBy(a => a.Genre, Cmp),
            SortField.PlayCount => desc ? list.OrderByDescending(a => a.PlayCount) : list.OrderBy(a => a.PlayCount),
            SortField.DateAdded => desc ? list.OrderByDescending(a => a.DateAdded) : list.OrderBy(a => a.DateAdded),
            // Track Name and Album Name both sort albums by title.
            _ => desc ? list.OrderByDescending(a => a.Title, Cmp) : list.OrderBy(a => a.Title, Cmp),
        };
        return o.ThenBy(a => a.Year).ThenBy(a => a.Title, Cmp).ToList();
    }

    private void BuildRows()
    {
        var rows = new List<AlbumRow>();
        for (int i = 0; i < _albums.Count; i += _columns)
            rows.Add(new AlbumRow(_albums.GetRange(i, Math.Min(_columns, _albums.Count - i))));
        Rows = rows;
    }

    public void PlayFromTable(Track t)
    {
        int i = Tracks.IndexOf(t);
        if (i >= 0) _pb.PlayTracks(Tracks, i);
    }

    public void PlayFromAlbum(Track t)
    {
        var a = SelectedAlbum;
        if (a == null) return;
        int i = a.Tracks.IndexOf(t);
        if (i >= 0) _pb.PlayTracks(a.Tracks, i);
    }
}

public sealed class GenreChip : ObservableObject
{
    private readonly Action<GenreChip> _toggled;

    public GenreChip(string key, string name, int count, bool selected, Action<GenreChip> toggled)
    {
        Key = key;
        Name = name;
        _count = count;
        _isSelected = selected;
        _toggled = toggled;
    }

    /// <summary>Genre text as stored on tracks ("" for songs without a genre).</summary>
    public string Key { get; }
    public string Name { get; }

    private int _count;
    public int Count
    {
        get => _count;
        set { if (Set(ref _count, value)) { OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(Tooltip)); } }
    }

    public string CountText => _count.ToString("N0");
    public string Tooltip => $"{Name}: {_count:N0} songs (click to filter; pick several to mix)";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) _toggled(this); }
    }

    public void SetSilently(bool selected)
    {
        _isSelected = selected;
        OnPropertyChanged(nameof(IsSelected));
    }
}