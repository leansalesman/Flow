using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Flow.Library;
using Flow.Services;
using Flow.Spotify;

namespace Flow.Infrastructure;

/// <summary>
/// Developer aid: `Flow.exe --memory-test &lt;file&gt;` loads the library read-only, simulates scrolling the
/// album shelves (keeping ~40 covers "on screen"), and records memory at each step.
/// </summary>
public static class MemoryProbe
{
    public static void Run(string outFile)
    {
        var lines = new List<string>();
        var proc = Process.GetCurrentProcess();
        void Mark(string label)
        {
            proc.Refresh();
            lines.Add($"{label,-34} private={proc.PrivateMemorySize64 / 1048576,5} MB  workingSet={proc.WorkingSet64 / 1048576,5} MB  managed={GC.GetTotalMemory(false) / 1048576,4} MB");
        }

        Mark("start");
        var settings = new SettingsService();
        settings.Load();
        var lib = new LibraryService(settings, settings.DataDir);
        lib.LoadFromDatabase();
        var cachePath = Path.Combine(settings.DataDir, "spotify_library.json");
        if (File.Exists(cachePath))
        {
            var cache = System.Text.Json.JsonSerializer.Deserialize<SpotifyLibraryCache>(File.ReadAllText(cachePath))!;
            lib.SetSpotifyTracks(cache.Tracks.Select(SpotifyService.ToTrack));
        }
        Mark($"library loaded ({lib.Count} tracks)");

        var albums = lib.Snapshot().GroupBy(t => t.ArtKey).Select(g => new AlbumInfo(g.Key, g.ToList(), lib.Art)).ToList();
        Mark($"albums built ({albums.Count})");

        // Scroll through every shelf; the UI holds roughly 40 covers at a time.
        var onScreen = new Queue<ImageSource?>();
        long pixels = 0;
        foreach (var a in albums)
        {
            var img = a.Thumb;
            if (img is System.Windows.Media.Imaging.BitmapSource bs) pixels += (long)bs.PixelWidth * bs.PixelHeight;
            onScreen.Enqueue(img);
            if (onScreen.Count > 40) onScreen.Dequeue();
        }
        Mark($"scrolled all shelves (avg {Math.Sqrt(pixels / Math.Max(1, albums.Count)):0}px covers)");

        // Rebuild (as happens on sort/filter/search) and scroll again.
        albums = lib.Snapshot().GroupBy(t => t.ArtKey).Select(g => new AlbumInfo(g.Key, g.ToList(), lib.Art)).ToList();
        foreach (var a in albums) { onScreen.Enqueue(a.Thumb); if (onScreen.Count > 40) onScreen.Dequeue(); }
        Mark("rebuilt + scrolled again");

        // Open 30 albums' large artwork (album detail / Now Playing).
        var large = new List<ImageSource?>();
        foreach (var a in albums.Take(30)) { large.Add(a.LargeArt); if (large.Count > 1) large.RemoveAt(0); }
        Mark("opened 30 album details");

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        Mark("after full GC");

        GC.KeepAlive(onScreen);
        GC.KeepAlive(large);
        File.WriteAllLines(outFile, lines);
    }
}
