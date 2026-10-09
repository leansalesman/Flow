using System.IO;
using System.Windows;
using System.Windows.Threading;
using Flow.Audio;
using Flow.Interop;
using Flow.Library;
using Flow.Services;
using Flow.Spotify;
using Flow.ViewModels;

namespace Flow;

public partial class App : Application
{
    private SingleInstance? _single;
    private SettingsService? _settings;
    private LibraryService? _library;
    private PlaybackService? _playback;
    private MainViewModel? _vm;
    private DateTime _lastExternalOpen = DateTime.MinValue;
    private readonly List<string> _pendingExternal = new();
    private DispatcherTimer? _externalTimer;

    public ThemeService Theme { get; private set; } = null!;
    /// <summary>Built-in Spotify playback engine (librespot), when this run of Flow has one.</summary>
    public LibrespotHost? Librespot { get; private set; }
    /// <summary>Checks GitHub Releases for newer versions and installs them (Settings, About).</summary>
    public UpdateService Updates { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
#if DEBUG
        // Developer switches (Debug builds only). They read the real settings but must never write them.
        if (e.Args.Length > 0 && e.Args[0] is "--test-genres" or "--render-visualizers" or "--render-library" or "--test-sync"
                or "--test-tags" or "--test-update" or "--perf-switch" or "--memory-ui" or "--memory-test" or "--screenshots" or "--test-shuffle")
            SettingsService.ReadOnly = true;
        if (e.Args.Length == 2 && e.Args[0] == "--test-genres")
        {
            // Developer aid: dry-run genre lookups for a sample of the cached Spotify albums (read-only).
            try
            {
                Task.Run(() => TestGenres(e.Args[1])).GetAwaiter().GetResult();
            }
            catch (Exception ex) { File.WriteAllText(e.Args[1], ex.ToString()); }
            Shutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--render-visualizers")
        {
            try { Flow.Visualizer.VisualizerPreview.RenderAll(e.Args[1]); }
            catch (Exception ex) { Log(ex); }
            Shutdown();
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--render-library")
        {
            base.OnStartup(e);
            RunLibraryRender(e.Args[1], e.Args.Length > 2 ? e.Args[2] : null);
            return;
        }
#endif
        // Used by Flow-Setup: register / remove "Open with Flow" for audio files, then exit (no window).
        if (e.Args.Length == 1 && e.Args[0] is "--register-associations" or "--unregister-associations")
        {
            try
            {
                if (e.Args[0] == "--register-associations") FileAssociations.Register();
                else FileAssociations.Unregister();
            }
            catch (Exception ex) { Log(ex); }
            Shutdown();
            return;
        }
#if DEBUG
        if (e.Args.Length == 2 && e.Args[0] == "--test-shuffle")
        {
            base.OnStartup(e);
            RunShuffleProbe(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--screenshots")
        {
            base.OnStartup(e);
            RunScreenshots(e.Args[1]);
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--test-tags")
        {
            base.OnStartup(e);
            RunTagProbe(e.Args[1], e.Args.Length > 2 ? e.Args[2] : null);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--test-sync")
        {
            var outFile = e.Args[1];
            _ = Task.Run(async () =>
            {
                try
                {
                    var settings = new SettingsService();
                    var library = new LibraryService(settings, settings.DataDir);
                    var spotify = new SpotifyService(settings, library);
                    File.WriteAllText(outFile, await spotify.TestIncrementalSyncAsync());
                }
                catch (Exception ex) { File.WriteAllText(outFile, ex.ToString()); }
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }
        // Developer test: check GitHub, download and verify the latest setup (never installs), log the result.
        if (e.Args.Length == 2 && e.Args[0] == "--test-update")
        {
            var log = e.Args[1];
            _ = Task.Run(async () =>
            {
                var lines = new List<string> { $"current {UpdateService.CurrentVersion}" };
                File.WriteAllLines(log, lines);
                try
                {
                    var u = new UpdateService();
                    lines.Add("check (normal): " + ((await u.CheckAsync())?.Version.ToString() ?? "up to date"));
                    File.WriteAllLines(log, lines);
                    var latest = await u.CheckAsync(force: true);
                    lines.Add($"latest {latest?.Version} {latest?.SetupName} {latest?.Size} sha256={latest?.Sha256}");
                    if (latest != null)
                    {
                        double last = 0;
                        var path = await u.DownloadAsync(latest, new Progress<double>(p => last = p));
                        lines.Add($"downloaded + verified: {path} ({new FileInfo(path).Length} bytes, progress {last:P0})");
                        File.Delete(path);
                    }
                }
                catch (Exception ex) { lines.Add("FAILED: " + ex.Message); }
                File.WriteAllLines(log, lines);
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--perf-switch")
        {
            base.OnStartup(e);
            RunSwitchProbe(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--memory-ui")
        {
            base.OnStartup(e);
            RunUiProbe(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--memory-test")
        {
            try { Flow.Infrastructure.MemoryProbe.Run(e.Args[1]); }
            catch (Exception ex) { File.WriteAllText(e.Args[1], ex.ToString()); }
            Shutdown();
            return;
        }
#endif
        base.OnStartup(e);
        StartApp(e);
    }

#if DEBUG
    /// <summary>Developer aid: dry-run genre lookups for a sample of the cached Spotify albums (read-only).</summary>
    private static async Task TestGenres(string outFile)
    {
        var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flow", "spotify_library.json");
        var cache = System.Text.Json.JsonSerializer.Deserialize<SpotifyLibraryCache>(File.ReadAllText(cachePath))!;
        var lookup = new GenreLookup();
        var sample = cache.Tracks.GroupBy(t => t.AlbumId).Select(g => g.First()).OrderBy(_ => Random.Shared.Next()).Take(40).ToList();
        var lines = new List<string>();
        int found = 0;
        foreach (var t in sample)
        {
            bool comp = string.IsNullOrWhiteSpace(t.AlbumArtist) || t.AlbumArtist == "Various Artists";
            string g = comp ? "" : await lookup.ForAlbumAsync(t.AlbumArtist, t.Album);
            string how = "album";
            if (g.Length == 0) { g = await lookup.ForTrackAsync(t.Artist.Split(", ")[0], t.Title); how = "track"; }
            if (g.Length > 0) found++;
            lines.Add($"{(g.Length > 0 ? g : "(none)"),-16} via {how,-5} | {t.AlbumArtist} - {t.Album}");
        }
        lines.Insert(0, $"Found {found} of {sample.Count}");
        File.WriteAllLines(outFile, lines);
    }
#endif

    private void StartApp(StartupEventArgs e)
    {
        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            SingleInstance.SendToFirst(e.Args);
            Shutdown();
            return;
        }

        Flow.Spotify.SpotifyLog.Write($"Flow {UpdateService.CurrentVersion} started (pid {Environment.ProcessId}){(e.Args.Length > 0 ? " " + string.Join(' ', e.Args) : "")}");
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log(ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log(ex.Exception); ex.SetObserved(); };

        _settings = new SettingsService();
        _settings.Load();
        Theme = new ThemeService(Dispatcher, ThemeService.Parse(_settings.Current.Theme));

        _library = new LibraryService(_settings, _settings.DataDir);
        var engine = new AudioEngine();
        var spotify = new SpotifyService(_settings, _library);
        Librespot = new LibrespotHost(_settings, Dispatcher);
        _playback = new PlaybackService(engine, _library, _settings, new SpotifyPlayback(spotify, Dispatcher, _settings, Librespot), Dispatcher, Librespot);
        _vm = new MainViewModel(_playback, _library, _settings, spotify, Dispatcher);

        var window = new MainWindow(_vm, _settings, Theme);
        MainWindow = window;
        window.Show();

        try { _library.LoadFromDatabase(); } catch (Exception ex) { Log(ex); }
        try { spotify.LoadCache(); } catch (Exception ex) { Log(ex); }
        _vm.Playlists.LoadEntries();
        _library.RefreshWatchers();
        Flow.Interop.FileAssociations.RefreshIfMoved();   // keep "Open with Flow" pointing here if Flow was moved
        _ = _library.ScanAsync();
        if (spotify.SyncIsDue) _ = spotify.SyncAsync();

        var files = e.Args.Where(a => File.Exists(a) || Directory.Exists(a)).ToArray();
        if (files.Length > 0)
        {
            _lastExternalOpen = DateTime.UtcNow;
            _playback.PlayFiles(files);
        }
        else
        {
            _playback.Restore(play: e.Args.Contains("--resume"));
            if (_library.Count == 0 && _settings.Current.MusicFolders.Count == 0 && !_settings.Current.LastQueue.Any())
                _vm.ShowToast("Welcome to Flow — add a music folder or drop files here to begin");
        }

        // Startup creates a lot of short-lived data (database, Spotify cache, first layout); tidy up once it settles.
        var settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            Flow.Infrastructure.MemoryTrim.Request(Dispatcher, force: true);
            _ = DailyUpdateCheckAsync();
            // Built-in Spotify engine set to start with Flow (otherwise it starts on the first Spotify play).
            if (_settings.Current.SpotifyEngine == SpotifyEngine.BuiltIn && _settings.Current.LibrespotStartWithFlow)
                _ = Librespot?.EnsureRunningAsync();
        };
        settle.Start();

        // Pre-build the Library and Playlists screens once the window is up, so the first visit is instant.
        var warm = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(2) };
        warm.Tick += (_, _) => { warm.Stop(); try { window.WarmUpPages(); } catch (Exception ex) { Log(ex); } };
        warm.Start();

        _externalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _externalTimer.Tick += (_, _) => FlushExternal();
        _single.ArgumentsReceived += args => Dispatcher.BeginInvoke(() => QueueExternal(args));
        _single.StartServer();
    }

    /// <summary>Explorer launches one process per file with "Open with"; batch them into one queue.</summary>
    private void QueueExternal(string[] args)
    {
        if (MainWindow is MainWindow mw) mw.BringToFront();
        if (args.Length == 0) return;
        _pendingExternal.AddRange(args);
        _externalTimer!.Stop();
        _externalTimer.Start();
    }

    private void FlushExternal()
    {
        _externalTimer!.Stop();
        if (_pendingExternal.Count == 0 || _playback == null) return;
        var files = _pendingExternal.ToList();
        _pendingExternal.Clear();
        bool append = (DateTime.UtcNow - _lastExternalOpen).TotalSeconds < 3 && _playback.HasTrack;
        _lastExternalOpen = DateTime.UtcNow;
        _playback.PlayFiles(files, append);
    }

    /// <summary>At most once a day, quietly look for a newer Flow; say so if there is one (installing is the user's call).</summary>
    private async Task DailyUpdateCheckAsync()
    {
        var s = _settings.Current;
        if (!s.AutoCheckUpdates || (s.LastUpdateCheck is { } last && (DateTime.Now - last).TotalHours < 20)) return;
        try
        {
            var update = await Updates.CheckAsync();
            s.LastUpdateCheck = DateTime.Now;
            _settings.Save();
            if (update == null) return;
            _vm?.Settings.ShowAvailableUpdate(update);
            _vm?.ShowToast($"Flow {update.Version} is available. Install it from Settings, About.");
        }
        catch (Exception ex) { Flow.Infrastructure.DiagLog.Write("Update check failed: " + ex.Message); }
    }

    public void SaveAll()
    {
        try
        {
            _playback?.SaveState();
            _settings?.Save();
        }
        catch (Exception ex) { Log(ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SaveAll();
        try { _playback?.Dispose(); } catch { }
        try { Librespot?.Dispose(); } catch { }
        try { _library?.Dispose(); } catch { }
        _single?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        _vm?.ShowToast("Something went wrong: " + e.Exception.Message);
    }

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flow");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:u}] {ex}\r\n\r\n");
        }
        catch { }
    }
}
