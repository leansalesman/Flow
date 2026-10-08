#if DEBUG
// Developer aid, left out of release builds.
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flow.Library;
using Flow.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Flow;

public partial class App
{
    /// <summary>
    /// Developer test: `Flow.exe --test-tags &lt;folder&gt; [webp]` makes WAV/MP3/M4A test songs in the folder, scans them
    /// into a throwaway library (never the real one), edits details, reorders, sets a cover, then scans again from
    /// scratch to check everything was written into the files. Also renders the Edit info / Edit album dialogs.
    /// </summary>
    private async void RunTagProbe(string outDir, string? webp)
    {
        var log = new List<string>();
        void L(string s) { log.Add(s); File.WriteAllLines(Path.Combine(outDir, "tags.txt"), log); }
        try
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            var music = Path.Combine(outDir, "music");
            Directory.CreateDirectory(music);
            Theme = new ThemeService(Dispatcher, AppTheme.Dark);

            // Three short tones in three formats.
            var files = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                var wav = Path.Combine(music, $"tone{i + 1}.wav");
                var sig = new SignalGenerator(44100, 2) { Frequency = 220 * (i + 1), Gain = 0.2 }.Take(TimeSpan.FromSeconds(2));
                WaveFileWriter.CreateWaveFile16(wav, sig);
                string path = wav;
                if (i == 1) { path = Path.ChangeExtension(wav, ".mp3"); using var r = new WaveFileReader(wav); MediaFoundationEncoder.EncodeToMp3(r, path); }
                if (i == 2) { path = Path.ChangeExtension(wav, ".m4a"); using var r = new WaveFileReader(wav); MediaFoundationEncoder.EncodeToAac(r, path); }
                if (path != wav) File.Delete(wav);
                files.Add(path);
                var err = TagWriter.Apply(new TrackEdit(path)
                {
                    Title = $"Tone {i + 1}", Artist = "Test Artist", Album = "Old Album", AlbumArtist = "Test Artist", Year = 2001, TrackNumber = i + 1,
                });
                L($"made {Path.GetFileName(path)}: {err ?? "tagged"}");
            }

            var settings = new SettingsService();
            settings.Load();
            settings.Current.MusicFolders = new List<string> { music };   // in memory only: never saved
            using var lib = new LibraryService(settings, Path.Combine(outDir, "data1"));
            await lib.ScanAsync();
            Dump(L, "after first scan", lib);

            // Rename one song, then edit the album: new name/artist/year and the reverse order.
            var tracks = lib.Snapshot().OrderBy(t => t.TrackNumber).ToList();
            L("rename: " + string.Join("; ", await lib.EditAsync(new[] { new TrackEdit(tracks[0].Path) { Title = "Renamed Tone" } })));
            var edits = tracks.AsEnumerable().Reverse().Select((t, i) => new TrackEdit(t.Path)
            {
                Album = "New Album", AlbumArtist = "New Artist", Year = 2024, TrackNumber = i + 1, TrackCount = 3,
            }).ToList();
            L("album edit: " + string.Join("; ", await lib.EditAsync(edits)));
            Dump(L, "after edits (same library)", lib);

            // Cover: a transparent PNG, and a WebP when one is given.
            var png = Path.Combine(outDir, "cover.png");
            MakePng(png);
            var jpeg = TagWriter.PrepareCover(png);
            L($"png cover prepared: {jpeg.Length} bytes");
            if (webp != null)
            {
                try { jpeg = TagWriter.PrepareCover(webp); L($"webp cover prepared: {jpeg.Length} bytes"); }
                catch (Exception ex) { L("webp FAILED: " + ex.Message); }
            }
            L("cover: " + string.Join("; ", await lib.SetCoverAsync(lib.Snapshot(), jpeg)));
            var key = lib.Snapshot()[0].ArtKey;
            L($"art cache for album: thumb={File.Exists(lib.Art.ThumbPath(key))} large={File.Exists(lib.Art.LargePath(key))}");

            // Guards: Spotify songs and files in use.
            L("spotify guard: " + TagWriter.Apply(new TrackEdit("spotify:track:abc") { Title = "x" }));
            using (new FileStream(files[1], FileMode.Open, FileAccess.Read, FileShare.Read))
                L("file in use: " + TagWriter.Apply(new TrackEdit(files[1]) { Title = "x" }));

            // A brand-new library reading the files from scratch must see every change.
            using var lib2 = new LibraryService(settings, Path.Combine(outDir, "data2"));
            await lib2.ScanAsync();
            Dump(L, "fresh scan of the files", lib2);
            foreach (var f in files)
            {
                using var tf = TagLib.File.Create(f);
                L($"  {Path.GetFileName(f)} embedded pictures: {tf.Tag.Pictures.Length}");
            }

            // Render the dialogs off-screen.
            Views.FlowDialog.ProbeHook = w =>
            {
                w.Owner = null;
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = -30000; w.Top = -30000;
                w.ContentRendered += (_, _) =>
                {
                    Snap(w,Path.Combine(outDir, w.Title.Replace(' ', '_') + ".png"));
                    w.Close();
                };
            };
            var all = lib2.Snapshot().OrderBy(t => t.TrackNumber).ToList();
            Views.LocalEdits.EditInfo(null!, all[0]);
            Views.LocalEdits.EditAlbum(null!, all);
            L("dialogs rendered");
            L("DONE");
        }
        catch (Exception ex) { L("FAILED: " + ex); }
        Shutdown();
    }

    private static void Dump(Action<string> L, string label, LibraryService lib)
    {
        L(label + ":");
        foreach (var t in lib.Snapshot().OrderBy(t => t.TrackNumber))
            L($"  #{t.TrackNumber} {t.Title} | {t.Artist} | {t.Album} | {t.AlbumArtist} | {t.Year} | {Path.GetFileName(t.Path)}");
    }

    private static void MakePng(string path)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawEllipse(new LinearGradientBrush(Colors.MediumPurple, Colors.DeepSkyBlue, 45), null, new Point(200, 200), 180, 180);
        }
        var rtb = new RenderTargetBitmap(400, 400, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
#endif
