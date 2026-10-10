#if DEBUG
// Developer aid, left out of release builds.
using System.IO;
using Flow.Audio;
using Flow.Library;
using Flow.Services;
using Flow.Spotify;

namespace Flow;

public partial class App
{
    /// <summary>
    /// Developer test: `Flow.exe --test-shuffle &lt;file&gt;` plays a shuffled Spotify album on the built-in engine
    /// (muted, as its own Spotify device "Flow Test", never touching the real Flow), jumps near the end of each song
    /// and records how each song change went: whether Spotify went straight to Flow's next song or detoured into
    /// the album first, and how long the change took.
    /// </summary>
    private async void RunShuffleProbe(string outFile)
    {
        var log = new List<string>();
        void L(string s) { log.Add($"[{DateTime.Now:HH:mm:ss.fff}] {s}"); File.WriteAllLines(outFile, log); }
        LibrespotHost? librespot = null;
        PlaybackService? playback = null;
        var logStart = DateTime.Now;
        try
        {
            var settings = new SettingsService();
            settings.Load();
            var s = settings.Current;
            s.SpotifyEngine = SpotifyEngine.BuiltIn;
            s.LibrespotDeviceName = "Flow Test";   // its own device: the real Flow's "Flow" device is never used
            s.Volume = 0;
            s.Muted = true;
            s.Shuffle = true;
            s.CrossfadeSeconds = 0;

            var library = new LibraryService(settings, settings.DataDir);
            var engine = new AudioEngine();
            var spotify = new SpotifyService(settings, library);
            librespot = new LibrespotHost(settings, Dispatcher);
            playback = new PlaybackService(engine, library, settings, new SpotifyPlayback(spotify, Dispatcher, settings, librespot), Dispatcher, librespot);
            library.LoadFromDatabase();
            spotify.LoadCache();
            playback.Volume = 0;
            L($"engine built-in available: {LibrespotHost.IsAvailable}, volume {playback.Volume}, muted {playback.IsMuted}");

            // An album with at least five songs, played shuffled.
            var album = library.Snapshot().Where(t => t.IsSpotify && t.SpotifyAlbumUri != null)
                .GroupBy(t => t.SpotifyAlbumUri).Where(g => g.Count() >= 5).OrderByDescending(g => g.Count()).First()
                .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
            L($"album: {album[0].Album} by {album[0].DisplayAlbumArtist}, {album.Count} songs");
            playback.PlayTracks(album, 0, shuffle: true);
            L("queue order: " + string.Join(" | ", playback.Queue.Take(5).Select(t => $"{t.TrackNumber}. {t.Title}")));

            async Task<bool> WaitFor(Func<bool> cond, int seconds)
            {
                for (int i = 0; i < seconds * 10; i++) { if (cond()) return true; await Task.Delay(100); }
                return cond();
            }
            if (!await WaitFor(() => playback.IsPlaying && playback.PositionSeconds > 1, 40)) { L("FAILED: playback didn't start"); return; }
            L($"playing #{playback.CurrentIndex + 1}: {playback.CurrentTrack?.Title}");

            // Three song changes: jump near the end, then time how long until Flow's next song is playing.
            for (int change = 0; change < 3; change++)
            {
                var cur = playback.CurrentTrack!;
                int idx = playback.CurrentIndex;
                var expected = playback.Queue[idx + 1];
                playback.Seek(Math.Max(0, cur.Duration.TotalSeconds - 7));
                L($"jumped to 7 s before the end of \"{cur.Title}\"; next in Flow's order: \"{expected.Title}\" (album track {expected.TrackNumber})");
                var t0 = DateTime.Now;
                bool ok = await WaitFor(() => playback.CurrentTrack == expected && playback.PositionSeconds > 0.5, 40);
                L(ok ? $"  -> now \"{playback.CurrentTrack?.Title}\" after {(DateTime.Now - t0).TotalSeconds:0.0} s"
                     : $"  -> FAILED: still \"{playback.CurrentTrack?.Title}\"");
                await Task.Delay(1500);
            }

            // "Play next" mid-album must win over the list Spotify was given.
            var picked = album.First(t => t != playback.CurrentTrack && playback.Queue.IndexOf(t) > playback.CurrentIndex + 3);
            playback.PlayNext(new List<Track> { picked });
            var before = playback.CurrentTrack!;
            playback.Seek(Math.Max(0, before.Duration.TotalSeconds - 7));
            L($"Play next \"{picked.Title}\", jumped near the end of \"{before.Title}\"");
            var t1 = DateTime.Now;
            bool won = await WaitFor(() => playback.CurrentTrack == picked && playback.PositionSeconds > 0.5, 40);
            L(won ? $"  -> now \"{picked.Title}\" after {(DateTime.Now - t1).TotalSeconds:0.0} s (Play next honored)"
                  : $"  -> FAILED: \"{playback.CurrentTrack?.Title}\" instead of the Play next song");
            await Task.Delay(1500);

            // What the Spotify log says about those changes.
            var spotifyLog = Path.Combine(settings.DataDir, "spotify.log");
            var lines = File.ReadAllLines(spotifyLog).Where(x => x.Length > 25 && DateTime.TryParse(x[1..24], out var when) && when >= logStart).ToList();
            int plays = lines.Count(x => x.Contains("Play requested")), detours = lines.Count(x => x.Contains("Spotify continued with"));
            int fallbacks = lines.Count(x => x.Contains("Track list not started"));
            L($"Spotify log: {plays} play request(s), {detours} detour(s) into the album, {fallbacks} fallback(s) to album playback");
            foreach (var x in lines.Where(x => x.Contains("Play requested") || x.Contains("continued") || x.Contains("Track list") || x.Contains("Seek")))
                L("  " + x);
            L(fallbacks == 0 && log.Count(x => x.Contains("FAILED")) == 0 ? "PASS" : "CHECK");
        }
        catch (Exception ex) { L("FAILED: " + ex); }
        finally
        {
            try { playback?.Pause(); } catch { }
            await Task.Delay(1500);
            try { librespot?.Stop(); } catch { }
            L("DONE");
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Developer test: `Flow.exe --test-latency &lt;file&gt;` (built-in engine, muted, own "Flow Test" device): times how
    /// long a song takes to be heard after pressing play, split into Flow, Spotify and librespot.
    /// </summary>
    private async void RunLatencyProbe(string outFile)
    {
        var log = new List<string>();
        void L(string s) { log.Add($"[{DateTime.Now:HH:mm:ss.fff}] {s}"); File.WriteAllLines(outFile, log); }
        LibrespotHost? librespot = null;
        PlaybackService? playback = null;
        try
        {
            var settings = new SettingsService();
            settings.Load();
            var s = settings.Current;
            s.SpotifyEngine = SpotifyEngine.BuiltIn;
            s.LibrespotDeviceName = "Flow Test";
            s.Volume = 0; s.Muted = true; s.Shuffle = false; s.CrossfadeSeconds = 0;
            var library = new LibraryService(settings, settings.DataDir);
            var engine = new AudioEngine();
            var spotify = new SpotifyService(settings, library);
            librespot = new LibrespotHost(settings, Dispatcher);
            playback = new PlaybackService(engine, library, settings, new SpotifyPlayback(spotify, Dispatcher, settings, librespot), Dispatcher, librespot);
            library.LoadFromDatabase();
            spotify.LoadCache();
            playback.Volume = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await librespot.EnsureRunningAsync();
            L($"librespot started and ready in {sw.ElapsedMilliseconds} ms (happens once, when Flow first plays Spotify)");
            await Task.Delay(3000);

            var album = library.Snapshot().Where(t => t.IsSpotify && t.SpotifyAlbumUri != null)
                .GroupBy(t => t.SpotifyAlbumUri).Where(g => g.Count() >= 6).First()
                .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
            var input = librespot.Input;

            // Time from the button until Spotify reports the new song playing (the same yardstick for every method).
            async Task Measure(string what, Action start, Func<string?> target)
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                start();
                await Task.Delay(30);
                var want = target();
                long switched = -1;
                while (t.ElapsedMilliseconds < 15000)
                {
                    var r = await spotify.GetAsync("/me/player");
                    var uri = r.Json?["item"]?["uri"]?.GetValue<string>();
                    long prog = r.Json?["progress_ms"]?.GetValue<long>() ?? 99999;
                    if (uri == want && prog < 6000 && (r.Json?["is_playing"]?.GetValue<bool>() ?? false)) { switched = t.ElapsedMilliseconds; break; }
                    await Task.Delay(60);
                }
                L($"{what}: Spotify playing the new song after {(switched < 0 ? "(never)" : switched + " ms")}");
            }

            await Measure("Play an album (play command)", () => playback.PlayTracks(album, 0), () => album[0].SpotifyUri);
            await Task.Delay(5000);
            for (int i = 0; i < 3; i++)
            {
                await Measure("Next (Spotify's own next, preloaded)", () => playback.Next(), () => playback.CurrentTrack?.SpotifyUri);
                await Task.Delay(5000);
            }
            for (int i = 0; i < 2; i++)
            {
                int target = playback.CurrentIndex + 2;
                await Measure("Jump two songs ahead (play command)", () => playback.PlayAt(target), () => playback.Queue[target].SpotifyUri);
                await Task.Delay(5000);
            }
            foreach (var x in File.ReadAllLines(Path.Combine(settings.DataDir, "spotify.log")).TakeLast(30).Where(x => x.Contains("Next") || x.Contains("Devices")))
                L("    " + x);
        }
        catch (Exception ex) { L("FAILED: " + ex); }
        finally
        {
            try { playback?.Pause(); } catch { }
            await Task.Delay(1500);
            try { librespot?.Stop(); } catch { }
            L("DONE");
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Developer test: `Flow.exe --test-restart &lt;file&gt;` (built-in engine, muted, own device, EQ on): restarts
    /// librespot mid-song several times, like the old normalisation toggle did, and checks the audio after each
    /// restart is music again (no garbage, no stuck silence). Then checks a settings change waits for a pause.
    /// </summary>
    private async void RunRestartProbe(string outFile)
    {
        var log = new List<string>();
        void L(string s) { log.Add($"[{DateTime.Now:HH:mm:ss.fff}] {s}"); File.WriteAllLines(outFile, log); }
        LibrespotHost? librespot = null;
        PlaybackService? playback = null;
        try
        {
            var settings = new SettingsService();
            settings.Load();
            var s = settings.Current;
            s.SpotifyEngine = SpotifyEngine.BuiltIn;
            s.LibrespotDeviceName = "Flow Test";
            s.Volume = 0; s.Muted = true; s.Shuffle = false; s.CrossfadeSeconds = 0;
            s.EqEnabled = true; s.EqGains = new double[] { 5, 4, 1, 0, -2, 1, 0, 1, 4, 5 };
            var library = new LibraryService(settings, settings.DataDir);
            var engine = new AudioEngine();
            var spotify = new SpotifyService(settings, library);
            librespot = new LibrespotHost(settings, Dispatcher);
            playback = new PlaybackService(engine, library, settings, new SpotifyPlayback(spotify, Dispatcher, settings, librespot), Dispatcher, librespot);
            library.LoadFromDatabase();
            spotify.LoadCache();
            playback.Volume = 0;
            await librespot.EnsureRunningAsync();

            var album = library.Snapshot().Where(t => t.IsSpotify && t.SpotifyAlbumUri != null)
                .GroupBy(t => t.SpotifyAlbumUri).Where(g => g.Count() >= 6).First()
                .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
            var wave = new float[2048];
            string Signal()
            {
                engine.Analyzer.GetWaveform(wave);
                float peak = 0; bool bad = false;
                foreach (var v in wave) { if (!float.IsFinite(v)) bad = true; else peak = Math.Max(peak, Math.Abs(v)); }
                return bad ? "GARBAGE (not finite)" : peak > 1.5f ? $"GARBAGE (peak {peak:0.0})" : peak < 0.005f ? "silent" : $"music (peak {peak:0.00})";
            }
            playback.PlayTracks(album, 0);
            await Task.Delay(6000);
            L("before any restart: " + Signal());
            bool ok = true;
            for (int i = 1; i <= 3; i++)
            {
                librespot.Stop();          // what the old toggle did mid-song
                await Task.Delay(300);
                await librespot.EnsureRunningAsync();
                await Task.Delay(4000);                          // the new process registers with Spotify Connect
                playback.PlayAt(playback.CurrentIndex);          // Flow starts the song again on the new process
                await Task.Delay(7000);
                var sig = Signal();
                ok &= sig.StartsWith("music");
                L($"after restart {i}: {sig}");
            }
            // A settings change while playing must wait for a pause.
            s.LibrespotNormalisation = true;
            librespot.ApplySettings();
            await Task.Delay(3000);
            L("settings changed while playing: " + Signal() + " (expected: still music, restart deferred)");
            playback.Pause();
            await Task.Delay(6000);
            playback.PlayPause();                               // play again after the deferred restart
            await Task.Delay(9000);
            var after = Signal();
            ok &= after.StartsWith("music");
            L("paused (restart applied), then play again: " + after);
            var lr = File.ReadAllLines(Path.Combine(settings.DataDir, "librespot.log")).TakeLast(4);
            foreach (var x in lr) L("    librespot.log: " + x);
            L(ok ? "PASS" : "CHECK");
        }
        catch (Exception ex) { L("FAILED: " + ex); }
        finally
        {
            try { playback?.Pause(); } catch { }
            await Task.Delay(1000);
            try { librespot?.Stop(); } catch { }
            L("DONE");
            Environment.Exit(0);
        }
    }
}
#endif
