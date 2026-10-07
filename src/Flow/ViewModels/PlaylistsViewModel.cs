using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Flow.Infrastructure;
using Flow.Library;
using Flow.Services;

namespace Flow.ViewModels;

public enum SmartKind { None, Favorites, RecentlyAdded, MostPlayed, RecentlyPlayed, Spotify }

public sealed class PlaylistEntry : ObservableObject
{
    public long Id { get; init; }
    public string? SpotifyId { get; init; }
    public SmartKind Smart { get; init; }
    public bool IsSmart => Smart != SmartKind.None;
    public string Glyph { get; init; } = "";

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }
}

public sealed class PlaylistsViewModel : ObservableObject
{
    private readonly LibraryService _lib;
    private readonly PlaybackService _pb;
    private readonly Dispatcher _ui;
    private readonly Action<string> _toast;
    private readonly Func<IReadOnlyList<Flow.Spotify.SpotifyPlaylist>> _spotifyPlaylists;

    private readonly SettingsService _settings;

    public PlaylistsViewModel(LibraryService lib, PlaybackService pb, Dispatcher ui, Action<string> toast,
                              Func<IReadOnlyList<Flow.Spotify.SpotifyPlaylist>> spotifyPlaylists, SettingsService settings)
    {
        _lib = lib;
        _pb = pb;
        _ui = ui;
        _toast = toast;
        _spotifyPlaylists = spotifyPlaylists;
        _settings = settings;
        _artSize = settings.Current.PlaylistArtSize is "Small" or "Large" ? settings.Current.PlaylistArtSize : "Medium";

        PlayCommand = new RelayCommand(() => { if (Tracks.Count > 0) _pb.PlayTracks(Tracks.ToList(), 0); });
        ShuffleCommand = new RelayCommand(() => { if (Tracks.Count > 0) _pb.PlayTracks(Tracks.ToList(), 0, shuffle: true); });

        _lib.Changed += () => _ui.BeginInvoke(() => { if (Selected?.IsSmart == true) LoadTracks(); });
        LoadEntries();
    }

    public ICommand PlayCommand { get; }
    public ICommand ShuffleCommand { get; }

    public ObservableCollection<PlaylistEntry> Entries { get; } = new();
    public ObservableCollection<Track> Tracks { get; } = new();

    public IEnumerable<PlaylistEntry> UserPlaylists => Entries.Where(e => !e.IsSmart);

    private PlaylistEntry? _selected;
    public PlaylistEntry? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(IsUserPlaylist));
            LoadTracks();
        }
    }

    public bool IsUserPlaylist => _selected is { IsSmart: false };

    // ---- Artwork size (Small / Medium / Large) ----

    private string _artSize = "Medium";
    public string ArtSize
    {
        get => _artSize;
        set
        {
            value = value is "Small" or "Large" ? value : "Medium";
            if (!Set(ref _artSize, value)) return;
            _settings.Current.PlaylistArtSize = value;
            OnPropertyChanged(nameof(RowThumb));
            OnPropertyChanged(nameof(RowHeight));
        }
    }

    public double RowThumb => _artSize switch { "Small" => 30, "Large" => 72, _ => 44 };
    public double RowHeight => RowThumb + 12;

    private string _info = "";
    public string InfoText { get => _info; private set => Set(ref _info, value); }

    public void LoadEntries()
    {
        var keepId = _selected?.Id;
        var keepSmart = _selected?.Smart;
        var keepSpotify = _selected?.SpotifyId;
        Entries.Clear();
        Entries.Add(new PlaylistEntry { Id = -1, Smart = SmartKind.Favorites, Name = "Favorites", Glyph = "" });
        Entries.Add(new PlaylistEntry { Id = -2, Smart = SmartKind.RecentlyAdded, Name = "Recently Added", Glyph = "" });
        Entries.Add(new PlaylistEntry { Id = -3, Smart = SmartKind.MostPlayed, Name = "Most Played", Glyph = "" });
        Entries.Add(new PlaylistEntry { Id = -4, Smart = SmartKind.RecentlyPlayed, Name = "Recently Played", Glyph = "" });
        foreach (var p in _lib.Db.GetPlaylists())
            Entries.Add(new PlaylistEntry { Id = p.Id, Name = p.Name, Glyph = "" });
        foreach (var sp in _spotifyPlaylists())
            Entries.Add(new PlaylistEntry { Id = -100, Smart = SmartKind.Spotify, SpotifyId = sp.Id, Name = sp.Name, Glyph = "" });
        OnPropertyChanged(nameof(UserPlaylists));
        Selected = Entries.FirstOrDefault(e => e.Id == keepId && e.Smart == keepSmart && e.SpotifyId == keepSpotify) ?? Entries.First();
    }

    public void LoadTracks()
    {
        Tracks.Clear();
        var sel = _selected;
        if (sel == null) { InfoText = ""; return; }
        IEnumerable<Track> items;
        var all = _lib.Snapshot();
        switch (sel.Smart)
        {
            case SmartKind.Favorites:
                items = all.Where(t => t.IsFavorite).OrderBy(t => t.DisplayArtist).ThenBy(t => t.Album).ThenBy(t => t.TrackNumber);
                break;
            case SmartKind.RecentlyAdded:
                items = all.OrderByDescending(t => t.DateAdded).Take(250);
                break;
            case SmartKind.MostPlayed:
                items = all.Where(t => t.PlayCount > 0).OrderByDescending(t => t.PlayCount).ThenByDescending(t => t.LastPlayed).Take(100);
                break;
            case SmartKind.RecentlyPlayed:
                items = all.Where(t => t.LastPlayed != null).OrderByDescending(t => t.LastPlayed).Take(100);
                break;
            case SmartKind.Spotify:
                var pl = _spotifyPlaylists().FirstOrDefault(p => p.Id == sel.SpotifyId);
                items = (pl?.Uris ?? new List<string>()).Select(u => _lib.Find(u)).Where(t => t != null)!;
                break;
            default:
                items = _lib.Db.GetPlaylistPaths(sel.Id).Select(p => _lib.Find(p) ?? (File.Exists(p) ? _lib.GetOrRead(p) : null))
                    .Where(t => t != null)!;
                break;
        }
        foreach (var t in items) Tracks.Add(t);
        var total = TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks));
        InfoText = Tracks.Count == 0 ? "No tracks yet" : $"{Tracks.Count} track{(Tracks.Count == 1 ? "" : "s")}  ·  {TimeFormat.Long(total)}";
    }

    public long CreatePlaylist(string name, IEnumerable<Track>? tracks = null)
    {
        var id = _lib.Db.CreatePlaylist(name);
        if (tracks != null) _lib.Db.SetPlaylistPaths(id, tracks.Select(t => t.Path));
        LoadEntries();
        Selected = Entries.FirstOrDefault(e => e.Id == id);
        return id;
    }

    public void Rename(PlaylistEntry e, string name)
    {
        if (e.IsSmart || string.IsNullOrWhiteSpace(name)) return;
        _lib.Db.RenamePlaylist(e.Id, name.Trim());
        e.Name = name.Trim();
    }

    public void Delete(PlaylistEntry e)
    {
        if (e.IsSmart) return;
        _lib.Db.DeletePlaylist(e.Id);
        if (ReferenceEquals(_selected, e)) _selected = null;
        LoadEntries();
    }

    public void AddTo(long playlistId, IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        var paths = _lib.Db.GetPlaylistPaths(playlistId);
        paths.AddRange(list.Select(t => t.Path));
        _lib.Db.SetPlaylistPaths(playlistId, paths);
        var name = Entries.FirstOrDefault(e => e.Id == playlistId)?.Name ?? "playlist";
        _toast(list.Count == 1 ? $"Added “{list[0].Title}” to {name}" : $"Added {list.Count} tracks to {name}");
        if (_selected?.Id == playlistId) LoadTracks();
    }

    public void RemoveFromSelected(IEnumerable<Track> tracks)
    {
        if (_selected is not { IsSmart: false } sel) return;
        foreach (var t in tracks.ToList()) Tracks.Remove(t);
        _lib.Db.SetPlaylistPaths(sel.Id, Tracks.Select(t => t.Path));
        LoadTracks();
    }

    public void Move(int from, int to)
    {
        if (_selected is not { IsSmart: false } sel) return;
        if (from < 0 || to < 0 || from >= Tracks.Count || to >= Tracks.Count || from == to) return;
        Tracks.Move(from, to);
        _lib.Db.SetPlaylistPaths(sel.Id, Tracks.Select(t => t.Path));
    }

    public void Import(string file)
    {
        var paths = PlaylistFile.Read(file);
        var name = Path.GetFileNameWithoutExtension(file);
        var id = _lib.Db.CreatePlaylist(name);
        _lib.Db.SetPlaylistPaths(id, paths);
        LoadEntries();
        Selected = Entries.FirstOrDefault(e => e.Id == id);
        _toast($"Imported “{name}” ({paths.Count} tracks)");
    }

    public void Export(string file)
    {
        PlaylistFile.Write(file, Tracks);
        _toast($"Exported to {Path.GetFileName(file)}");
    }

    public void PlayFrom(Track t)
    {
        int i = Tracks.IndexOf(t);
        if (i >= 0) _pb.PlayTracks(Tracks.ToList(), i);
    }
}
