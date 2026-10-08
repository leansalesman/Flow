using System.Windows.Input;
using System.Windows.Threading;
using Flow.Infrastructure;
using Flow.Library;
using Flow.Services;
using Microsoft.Win32;

namespace Flow.ViewModels;

public enum AppPage { NowPlaying, Library, Playlists, Search, Settings }

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _toastTimer;
    private readonly SettingsService _settingsRef;

    public MainViewModel(PlaybackService playback, LibraryService library, SettingsService settings,
                         Flow.Spotify.SpotifyService spotify, Dispatcher ui)
    {
        Playback = playback;
        LibraryService = library;
        Spotify = spotify;
        _settingsRef = settings;
        Library = new LibraryViewModel(library, playback, settings, ui, spotify);
        Playlists = new PlaylistsViewModel(library, playback, ui, ShowToast, () => spotify.Cache.Playlists, settings);
        Search = new SearchViewModel(spotify, playback, library, album =>
        {
            // Albums found on Spotify open in the Library's album spotlight.
            CurrentPage = AppPage.Library;
            Library.SelectedArtist = null;
            Library.SelectedAlbum = album;
        }, ui);
        Settings = new SettingsViewModel(settings, library, playback, spotify, ShowToast);
        playback.Notify += ShowToast;
        spotify.LibraryImported += () => ui.BeginInvoke(() =>
        {
            Playlists.LoadEntries();
            if (spotify.IsConnected) ShowToast($"Spotify library synced · {library.SpotifyCount:N0} songs");
            Flow.Infrastructure.MemoryTrim.Request(ui, force: true); // a sync leaves lots of temporary data behind
        });

        _toastTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast = null; };

        PlayPauseCommand = new RelayCommand(playback.PlayPause);
        NextCommand = new RelayCommand(playback.Next);
        PreviousCommand = new RelayCommand(playback.Previous);
        ShuffleCommand = new RelayCommand(() => playback.Shuffle = !playback.Shuffle);
        RepeatCommand = new RelayCommand(playback.CycleRepeat);
        FavoriteCommand = new RelayCommand(() => playback.ToggleFavorite());
        MuteCommand = new RelayCommand(() => playback.IsMuted = !playback.IsMuted);
        ToggleQueueCommand = new RelayCommand(() => IsQueueOpen = !IsQueueOpen);
        ClearQueueCommand = new RelayCommand(playback.ClearQueue);
        NavigateCommand = new RelayCommand(p =>
        {
            if (p is AppPage page) CurrentPage = page;
            else if (p is string s && Enum.TryParse<AppPage>(s, out var pg)) CurrentPage = pg;
        });
        OpenFilesCommand = new RelayCommand(OpenFiles);
        OpenQueueCommand = new RelayCommand(() => { CurrentPage = AppPage.NowPlaying; IsQueueOpen = true; });
        playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackService.HasTrack) || e.PropertyName == nameof(PlaybackService.CurrentTrack))
                OnPropertyChanged(nameof(ShowPlayerBar));
        };
        // Links from Now Playing jump into the Library's artist page / album spotlight.
        ShowArtistCommand = new RelayCommand(p =>
        {
            if (p is not string name) return;
            CurrentPage = AppPage.Library;
            Library.OpenArtist(name);
        });
        ShowAlbumCommand = new RelayCommand(p =>
        {
            if (p is not Track t) return;
            CurrentPage = AppPage.Library;
            Library.SelectedArtist = null;
            Library.OpenAlbumOf(t);
        });
        AddFolderCommand = new RelayCommand(Settings.AddFolder);
    }

    public PlaybackService Playback { get; }
    public LibraryService LibraryService { get; }
    public Flow.Spotify.SpotifyService Spotify { get; }
    public LibraryViewModel Library { get; }
    public PlaylistsViewModel Playlists { get; }
    public SearchViewModel Search { get; }
    public SettingsViewModel Settings { get; }

    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand ShuffleCommand { get; }
    public ICommand RepeatCommand { get; }
    public ICommand FavoriteCommand { get; }
    public ICommand MuteCommand { get; }
    public ICommand ToggleQueueCommand { get; }
    public ICommand ClearQueueCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand OpenFilesCommand { get; }
    public ICommand ShowArtistCommand { get; }
    public ICommand ShowAlbumCommand { get; }
    public ICommand AddFolderCommand { get; }

    // ---- Visualizer style ----

    private static readonly Flow.Visualizer.VisualizerStyle[] StyleCycle = Enum.GetValues<Flow.Visualizer.VisualizerStyle>();

    public Flow.Visualizer.VisualizerStyle VisualizerStyle
    {
        get => Enum.TryParse<Flow.Visualizer.VisualizerStyle>(_settingsRef.Current.VisualizerStyle, out var s) ? s : default;
        set
        {
            _settingsRef.Current.VisualizerStyle = value.ToString();
            OnPropertyChanged();
        }
    }

    public void CycleVisualizer(int dir)
    {
        int i = Array.IndexOf(StyleCycle, VisualizerStyle);
        VisualizerStyle = StyleCycle[(i + dir + StyleCycle.Length) % StyleCycle.Length];
    }

    private AppPage _page = AppPage.NowPlaying;
    public AppPage CurrentPage
    {
        get => _page;
        set { if (Set(ref _page, value)) OnPropertyChanged(nameof(ShowPlayerBar)); }
    }

    /// <summary>The bottom player bar: on every page except Now Playing, once something is loaded.</summary>
    public bool ShowPlayerBar => _page != AppPage.NowPlaying && Playback.HasTrack;

    public ICommand OpenQueueCommand { get; private set; } = null!;

    private bool _isQueueOpen;
    public bool IsQueueOpen { get => _isQueueOpen; set => Set(ref _isQueueOpen, value); }

    private string? _toast;
    public string? Toast { get => _toast; private set => Set(ref _toast, value); }

    public void ShowToast(string message)
    {
        Toast = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OpenFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open audio files",
            Filter = AudioFormats.DialogFilter,
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) Playback.PlayFiles(dlg.FileNames);
    }
}
