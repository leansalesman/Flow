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
}
#endif
