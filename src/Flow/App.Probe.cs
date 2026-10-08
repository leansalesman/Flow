using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Flow.Audio;
using Flow.Library;
using Flow.Services;
using Flow.Spotify;
using Flow.ViewModels;
using Flow.Visualizer;

namespace Flow;

public partial class App
{
    /// <summary>
    /// Developer aid: `Flow.exe --memory-ui &lt;file&gt;` runs the real window off-screen (separate from any running
    /// Flow; no playback, nothing saved), exercises the main screens and records memory after each step.
    /// </summary>
    private async void RunUiProbe(string outFile)
    {
        var lines = new List<string>();
        var proc = Process.GetCurrentProcess();
        void Mark(string label)
        {
            proc.Refresh();
            lines.Add($"{label,-38} private={proc.PrivateMemorySize64 / 1048576,5} MB  ws={proc.WorkingSet64 / 1048576,5} MB  managed={GC.GetTotalMemory(false) / 1048576,4} MB");
            File.WriteAllLines(outFile, lines);
        }
        Task Wait(int ms) => Task.Delay(ms);

        try
        {
            Mark("start");
            Flow.MainWindow.ProbeMode = true;
            var settings = new SettingsService();
            settings.Load();
            Theme = new ThemeService(Dispatcher, ThemeService.Parse(Environment.GetEnvironmentVariable("FLOW_THEME") ?? settings.Current.Theme));
            var library = new LibraryService(settings, settings.DataDir);
            var engine = new AudioEngine();
            var spotify = new SpotifyService(settings, library);
            var playback = new PlaybackService(engine, library, settings, new SpotifyPlayback(spotify, Dispatcher), Dispatcher);
            var vm = new MainViewModel(playback, library, settings, spotify, Dispatcher);
            var window = new Flow.MainWindow(vm, settings, Theme)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000, Top = -30000, Width = 1900, Height = 1040,
                ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            library.LoadFromDatabase();
            spotify.LoadCache();
            await Wait(4000);
            Mark("window shown, library loaded");

            vm.CurrentPage = AppPage.Library;
            vm.Library.IsGridView = true;
            await Wait(3000);
            Mark("library shelves (top)");

            var sv = FindChild<ScrollViewer>(window.LibraryPage.Shelves);
            if (sv != null)
            {
                for (double y = 0; y <= sv.ScrollableHeight; y += sv.ViewportHeight * 0.8)
                {
                    sv.ScrollToVerticalOffset(y);
                    await Wait(120);
                }
                await Wait(1500);
                Mark("scrolled through all shelves");
                sv.ScrollToTop();
            }

            // Library rebuild churn: re-sorting/filtering 30 times (like clicking sort options and genre chips).
            var fields = Enum.GetValues<SortField>();
            for (int i = 0; i < 30; i++)
            {
                vm.Library.SetSort(fields[i % fields.Length]);
                await Wait(400);
                if (sv != null) sv.ScrollToVerticalOffset((i % 5) * sv.ViewportHeight);
                await Wait(300);
            }
            Mark("30 re-sorts while browsing");

            vm.Library.IsGridView = false;
            await Wait(2000);
            var tsv = FindChild<ScrollViewer>(window.LibraryPage.Table);
            if (tsv != null)
            {
                for (double y = 0; y <= tsv.ScrollableHeight; y += tsv.ViewportHeight * 0.9)
                {
                    tsv.ScrollToVerticalOffset(y);
                    await Wait(40);
                }
            }
            await Wait(1500);
            Mark("track table scrolled");
            vm.Library.IsGridView = true;

            for (int i = 0; i < 5 && i < vm.Library.Albums.Count; i++)
            {
                vm.Library.SelectedAlbum = vm.Library.Albums[i * 7 % vm.Library.Albums.Count];
                await Wait(800);
                vm.Library.SelectedAlbum = null;
                await Wait(500);
            }
            Mark("opened/closed 5 albums (blur)");

            vm.CurrentPage = AppPage.NowPlaying;
            var tracks = library.Snapshot();
            if (tracks.Count > 0)
            {
                // Show a track without playing it, so the art + visualizer are on screen.
                playback.Queue.Add(tracks[0]);
            }
            foreach (var style in Enum.GetValues<VisualizerStyle>())
            {
                settings.Current.VisualizerStyle = style.ToString();
                vm.VisualizerStyle = style;
                await Wait(1000);
            }
            Mark("cycled all visualizer styles");

            vm.CurrentPage = AppPage.Library;
            await Wait(1000);
            vm.CurrentPage = AppPage.NowPlaying;
            await Wait(2000);
            Mark("idle on Now Playing");

            // Simulated playback: feed the visualizer a synthetic signal (no audio output) and watch for growth.
            var viz = window.NowPlayingPage.Viz;
            viz.IsActive = true;
            var analyzer = engine.Analyzer;
            var buf = new float[1600];
            double phase = 0;
            var feeder = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            feeder.Tick += (_, _) =>
            {
                for (int i = 0; i < buf.Length; i += 2)
                {
                    phase += 1.0 / 48000;
                    double beat = phase * 2.1 % 1.0;
                    double s = (beat < 0.12 ? Math.Sin(2 * Math.PI * 60 * phase) * (1 - beat / 0.12) : 0) * 0.6
                               + 0.2 * Math.Sin(2 * Math.PI * 330 * phase) + 0.05 * Math.Sin(2 * Math.PI * 2500 * phase);
                    buf[i] = buf[i + 1] = (float)s;
                }
                analyzer.Write(buf, 0, buf.Length);
            };
            feeder.Start();
            foreach (var style in Enum.GetValues<VisualizerStyle>().Where(s => s != VisualizerStyle.Off))
            {
                vm.VisualizerStyle = style;
                await Wait(5000);
                Mark($"playing: {style}");
            }
            vm.VisualizerStyle = VisualizerStyle.MirroredBlocks;
            for (int i = 1; i <= 3; i++) { await Wait(10000); Mark($"playing: Mirrored Blocks +{i * 10}s"); }
            feeder.Stop();

            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            await Wait(1000);
            Mark("after full GC");
        }
        catch (Exception ex)
        {
            lines.Add(ex.ToString());
            File.WriteAllLines(outFile, lines);
        }
        Environment.Exit(0); // never save settings from the probe
    }

    /// <summary>
    /// Developer aid: `Flow.exe --render-library &lt;folder&gt;` renders the real Library screens off-screen
    /// (track table, an artist page, an album opened from it) to PNGs and logs binding errors. Read-only.
    /// </summary>
    private async void RunLibraryRender(string outDir, string? artist)
    {
        Directory.CreateDirectory(outDir);
        var bindingLog = Path.Combine(outDir, "binding-errors.txt");
        System.Diagnostics.PresentationTraceSources.Refresh();
        var listener = new TextWriterTraceListener(bindingLog);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            Flow.MainWindow.ProbeMode = true;
            var settings = new SettingsService();
            settings.Load();
            if (Environment.GetEnvironmentVariable("FLOW_PROBE_BUILTIN") == "1") settings.Current.SpotifyEngine = SpotifyEngine.BuiltIn;
            // A visible EQ curve for checking the sliders (in memory only; the probe never saves).
            if (Environment.GetEnvironmentVariable("FLOW_PROBE_EQ") == "1") settings.Current.EqGains = new double[] { 5, 4, 1, 0, -2, 1, 0, 1, 4, 5 };
            Theme = new ThemeService(Dispatcher, ThemeService.Parse(Environment.GetEnvironmentVariable("FLOW_THEME") ?? settings.Current.Theme));
            var library = new LibraryService(settings, settings.DataDir);
            var engine = new AudioEngine();
            var spotify = new SpotifyService(settings, library);
            var playback = new PlaybackService(engine, library, settings, new SpotifyPlayback(spotify, Dispatcher), Dispatcher);
            var vm = new MainViewModel(playback, library, settings, spotify, Dispatcher);
            var window = new Flow.MainWindow(vm, settings, Theme)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000, Top = -30000, Width = 1600, Height = 1000,
                ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            library.LoadFromDatabase();
            spotify.LoadCache();
            playback.Restore(); // last queue, paused (as at startup) - so the player bar has a song to show
            vm.CurrentPage = AppPage.NowPlaying;
            await Task.Delay(3000);
            Snap(window, Path.Combine(outDir, "0_nowplaying.png"));
            vm.CurrentPage = AppPage.Library;
            vm.Library.IsGridView = false;
            vm.Library.SetSort(SortField.ArtistName);
            await Task.Delay(4000);
            Snap(window, Path.Combine(outDir, "1_table.png"));

            var name = artist ?? library.Snapshot().GroupBy(t => t.ArtistList.FirstOrDefault() ?? "")
                .OrderByDescending(g => g.Count()).First().Key;
            vm.Library.OpenArtist(name);
            await Task.Delay(2500);
            Snap(window, Path.Combine(outDir, "2_artist.png"));

            var album = vm.Library.SelectedArtist?.Albums.FirstOrDefault() ?? vm.Library.SelectedArtist?.AppearsOn.FirstOrDefault();
            if (album != null) vm.Library.SelectedAlbum = album;
            await Task.Delay(2000);
            Snap(window, Path.Combine(outDir, "3_album_from_artist.png"));

            vm.Library.SelectedAlbum = null; // Back → artist page again
            await Task.Delay(1200);

            vm.CurrentPage = AppPage.Settings;
            await Task.Delay(1500);
            Snap(window, Path.Combine(outDir, "4_settings.png"));

            // Spotify card, scrolled into view (FLOW_PROBE_BUILTIN=1 shows the built-in engine options; never saved).
            var engineLabel = FindText(window.SettingsPage, "Playback engine");
            if (engineLabel != null)
            {
                engineLabel.BringIntoView(new Rect(0, -260, 10, 620));
                await Task.Delay(800);
                Snap(window, Path.Combine(outDir, "4b_settings_spotify.png"));
            }
            var eqLabel = FindText(window.SettingsPage, "Equalizer");
            if (eqLabel != null)
            {
                eqLabel.BringIntoView(new Rect(0, -40, 10, 420));
                await Task.Delay(800);
                Snap(window, Path.Combine(outDir, "4c_settings_eq.png"));
            }
            var updatesButton = FindText(window.SettingsPage, "Check for updates");
            if (updatesButton != null)
            {
                updatesButton.BringIntoView(new Rect(0, -200, 10, 520));
                await Task.Delay(800);
                Snap(window, Path.Combine(outDir, "4d_settings_about.png"));
            }

            // Artwork sizes: shelves, track table, playlist rows.
            vm.Library.SelectedArtist = null;
            vm.CurrentPage = AppPage.Library;
            vm.Library.IsGridView = true;
            foreach (var size in new[] { "Small", "Large" })
            {
                vm.Library.ArtSize = size;
                await Task.Delay(2500);
                Snap(window, Path.Combine(outDir, $"5_shelves_{size}.png"));
            }
            vm.Library.IsGridView = false;
            await Task.Delay(2500);
            Snap(window, Path.Combine(outDir, "6_table_Large.png"));

            // Album spotlight + artist page text sizes.
            vm.Library.ArtSize = "Medium";
            var bigAlbum = library.Snapshot().GroupBy(t => t.ArtKey).Where(g => g.Count() is >= 10 and <= 30)
                .OrderByDescending(g => g.Count()).FirstOrDefault()?.First();
            if (bigAlbum != null)
            {
                vm.Library.OpenAlbumOf(bigAlbum);
                foreach (var size in new[] { "Small", "Medium", "Large" })
                {
                    vm.Library.TextSize = size;
                    await Task.Delay(2000);
                    Snap(window, Path.Combine(outDir, $"8_spotlight_{size}.png"));
                }
                // "Show full album" on a Spotify album the library has only one song of (reads from Spotify only).
                var single = library.Snapshot().Where(t => t.SpotifyAlbumUri != null)
                    .GroupBy(t => t.SpotifyAlbumUri).FirstOrDefault(g => g.Count() == 1)?.First();
                if (single != null)
                {
                    vm.Library.TextSize = "Medium";
                    vm.Library.OpenAlbumOf(single);
                    await Task.Delay(1500);
                    Snap(window, Path.Combine(outDir, "8c_album_partial.png"));
                    vm.Library.ShowFullAlbumCommand.Execute(null);
                    await Task.Delay(5000);
                    Snap(window, Path.Combine(outDir, "8d_album_full.png"));
                    File.AppendAllText(Path.Combine(outDir, "state.txt"),
                        $"full album: {single.Album} -> {vm.Library.SelectedAlbum?.Tracks.Count} tracks, full={vm.Library.SelectedAlbum?.IsFullAlbum}\n");
                }
                vm.Library.SelectedAlbum = null;
                vm.Library.OpenArtist(name);
                await Task.Delay(2000);
                Snap(window, Path.Combine(outDir, "9_artist_Large.png"));
                vm.Library.SelectedArtist = null;
            }

            vm.CurrentPage = AppPage.Playlists;
            vm.Playlists.LoadEntries(); // as App startup does after the Spotify cache loads
            var pl = vm.Playlists.Entries.FirstOrDefault(e => e.Name.Contains("Aphex", StringComparison.OrdinalIgnoreCase))
                     ?? vm.Playlists.Entries.FirstOrDefault(e => !e.IsSmart) ?? vm.Playlists.Entries.FirstOrDefault();
            vm.Playlists.Selected = pl;
            foreach (var size in new[] { "Small", "Large" })
            {
                vm.Playlists.ArtSize = size;
                await Task.Delay(2500);
                Snap(window, Path.Combine(outDir, $"7_playlist_{size}.png"));
            }

            // Search page: a real (read-only) Spotify search, then open the first album found.
            vm.CurrentPage = AppPage.Search;
            vm.Search.Query = Environment.GetEnvironmentVariable("FLOW_PROBE_SEARCH") ?? "aphex twin";
            await Task.Delay(5000);
            Snap(window, Path.Combine(outDir, "10_search.png"));
            var firstAlbum = vm.Search.Albums.FirstOrDefault();
            if (firstAlbum != null)
            {
                vm.Search.OpenAlbumCommand.Execute(firstAlbum);
                await Task.Delay(5000);
                Snap(window, Path.Combine(outDir, "10b_search_album.png"));
                vm.Library.SelectedAlbum = null;
            }
            File.WriteAllText(Path.Combine(outDir, "state.txt"),
                $"artist={name}\nafter Back: artistOverlay={vm.Library.ShowArtistOverlay} albumOpen={vm.Library.IsAlbumOpen}\n" +
                $"albums={vm.Library.SelectedArtist?.Albums.Count} appearsOn={vm.Library.SelectedArtist?.AppearsOn.Count} songs={vm.Library.SelectedArtist?.Tracks.Count}");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(outDir, "error.txt"), ex.ToString()); }
        listener.Flush();
        Environment.Exit(0);
    }

    private static void Snap(Window w, string file)
    {
        var root = (FrameworkElement)w.Content;
        int width = (int)root.ActualWidth, height = (int)root.ActualHeight;
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x28)), null, new Rect(0, 0, width, height));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(bg);
        rtb.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    private static TextBlock? FindText(DependencyObject root, string text)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is TextBlock tb && tb.Text == text) return tb;
            var r = FindText(c, text);
            if (r != null) return r;
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }
}
