using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Flow.Audio;
using Flow.Library;
using Flow.Services;
using Flow.Spotify;
using Flow.ViewModels;

namespace Flow;

public partial class App
{
    /// <summary>
    /// Developer aid: `Flow.exe --perf-switch &lt;file&gt;` runs the real window off-screen (separate from any running
    /// Flow; no playback, nothing saved) and times switching between Now Playing, Library and the album spotlight:
    /// how long the UI thread is blocked by each switch and how smooth the transition animation is.
    /// </summary>
    private async void RunSwitchProbe(string outFile)
    {
        var lines = new List<string>();
        void Write(string s) { lines.Add(s); File.WriteAllLines(outFile, lines); }

        // Frame timestamps from the UI thread's render loop.
        var frames = new List<double>();
        var clock = Stopwatch.StartNew();
        bool recording = false;
        CompositionTarget.Rendering += (_, _) => { if (recording) frames.Add(clock.Elapsed.TotalMilliseconds); };

        try
        {
            Flow.MainWindow.ProbeMode = true;
            var settings = new SettingsService();
            settings.Load();
            Theme = new ThemeService(Dispatcher);
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
            await Task.Delay(2000);
            window.WarmUpPages();   // as the app does after startup
            await Task.Delay(2000);
            vm.Library.IsGridView = true;
            var album = vm.Library.Albums.Count > 0 ? vm.Library.Albums[Math.Min(40, vm.Library.Albums.Count - 1)] : null;
            var track = album?.Tracks.FirstOrDefault();
            Write($"albums={vm.Library.Albums.Count} probe album={album?.Title} ({album?.Tracks.Count} tracks)");
            Write($"{"step",-34}{"blocked",9}{"settled",9}{"frames",8}{"maxGap",8}{"slow",6}{"gen2",6}");

            async Task Step(string name, Action action)
            {
                frames.Clear();
                int gen2 = GC.CollectionCount(2);
                recording = true;
                double t0 = clock.Elapsed.TotalMilliseconds;
                action();
                // Blocked: until the UI thread has done the layout/render work the switch queued.
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                double blocked = clock.Elapsed.TotalMilliseconds - t0;
                // Settled: until everything queued (including idle-time work) has run.
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                double settled = clock.Elapsed.TotalMilliseconds - t0;
                await Task.Delay(700); // let the transition animation play out
                recording = false;
                var pts = new List<double> { t0 };
                pts.AddRange(frames.Where(f => f <= t0 + 700));
                double maxGap = 0; int slow = 0;
                for (int i = 1; i < pts.Count; i++)
                {
                    double g = pts[i] - pts[i - 1];
                    maxGap = Math.Max(maxGap, g);
                    if (g > 34) slow++;
                }
                Write($"{name,-34}{blocked,9:F0}{settled,9:F0}{pts.Count - 1,8}{maxGap,8:F0}{slow,6}{GC.CollectionCount(2) - gen2,6}");
                await Task.Delay(600);
            }

            Write($"render tier={RenderCapability.Tier >> 16}  dpi={VisualTreeHelper.GetDpi(window).PixelsPerDip}");
            await Step("(idle baseline, Now Playing)", () => { });
            vm.CurrentPage = AppPage.Library; await Task.Delay(1500);
            await Step("(idle baseline, Library)", () => { });
            vm.CurrentPage = AppPage.NowPlaying; await Task.Delay(1500);
            int rounds = int.TryParse(Environment.GetEnvironmentVariable("FLOW_PERF_ROUNDS"), out var r) ? r : 3;
            for (int round = 1; round <= rounds; round++)
            {
                Write($"-- round {round}");
                await Step("Now Playing -> Library", () => vm.CurrentPage = AppPage.Library);
                if (album != null)
                {
                    await Step("Library -> Album spotlight", () => vm.Library.SelectedAlbum = album);
                    await Step("Album spotlight -> back", () => vm.Library.SelectedAlbum = null);
                }
                await Step("Library -> Now Playing", () => vm.CurrentPage = AppPage.NowPlaying);
                if (track != null)
                {
                    await Step("Now Playing -> Go to album", () => vm.ShowAlbumCommand.Execute(track));
                    await Step("Album -> Now Playing", () => vm.CurrentPage = AppPage.NowPlaying);
                }
                await Step("Now Playing -> Playlists", () => vm.CurrentPage = AppPage.Playlists);
                await Step("Playlists -> Now Playing", () => vm.CurrentPage = AppPage.NowPlaying);
                if (round < rounds) await Task.Delay(21000); // past MemoryTrim's 20 s throttle, like real use
            }
        }
        catch (Exception ex) { Write(ex.ToString()); }
        Environment.Exit(0); // never save settings from the probe
    }
}
