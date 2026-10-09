#if DEBUG
// Developer aid, left out of release builds.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    /// Developer aid: `Flow.exe --screenshots &lt;folder&gt;` renders the README screenshots off-screen from the real
    /// library (read-only; never saves settings): every theme, a lively visualizer, Electro and Alternative shelves,
    /// the album spotlight, an artist, Search, Settings (Spotify name blurred) and the mini player.
    /// </summary>
    private async void RunScreenshots(string outDir)
    {
        var log = new List<string>();
        void L(string s) { log.Add(s); File.WriteAllLines(Path.Combine(outDir, "screenshots.txt"), log); }
        try
        {
            Directory.CreateDirectory(outDir);
            Flow.MainWindow.ProbeMode = true;
            var settings = new SettingsService();
            settings.Load();
            Theme = new ThemeService(Dispatcher, AppTheme.DarkMetal);
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
            playback.Restore();
            await Task.Delay(2500);

            // A steady synthetic beat keeps the visualizer moving (nothing is audible).
            var analyzer = engine.Analyzer;
            var music = new SyntheticMusic();
            var feed = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
            feed.Tick += (_, _) => analyzer.Write(music.Next(1440), 0, 1440);
            feed.Start();
            MusicVisualizer? Viz() => Find<MusicVisualizer>(window);

            // ---- Now Playing in every theme ----
            vm.CurrentPage = AppPage.NowPlaying;
            vm.VisualizerStyle = VisualizerStyle.MirroredBlocks;
            foreach (var (theme, file) in new[] { (AppTheme.DarkMetal, "now-playing-dark-brushed-metal"), (AppTheme.Metal, "now-playing-brushed-metal"),
                                                  (AppTheme.Dark, "now-playing-dark"), (AppTheme.Light, "now-playing-light") })
            {
                Theme.SetMode(theme);
                await Task.Delay(400);
                if (Viz() is { } v) v.IsActive = true;
                await Task.Delay(1800);
                Snap(window, Path.Combine(outDir, file + ".png"));
                L(file);
                if (theme == AppTheme.Metal)
                {
                    // The social preview card uses the Brushed Metal shot, on brushed metal.
                    var metalBg = (Brush)Application.Current.Resources["WindowBackgroundBrush"];
                    SocialCard(Path.Combine(outDir, file + ".png"), metalBg, Path.Combine(outDir, "social-preview.png"));
                    L("social-preview: " + playback.CurrentTrack?.Title);
                }
            }
            Theme.SetMode(AppTheme.DarkMetal);

            // A few more visualizer styles.
            foreach (var (style, file) in new[] { (VisualizerStyle.SpectrumBars, "visualizer-spectrum-bars"), (VisualizerStyle.RadialRing, "visualizer-radial-ring"),
                                                  (VisualizerStyle.Particles, "visualizer-particles") })
            {
                vm.VisualizerStyle = style;
                await Task.Delay(400);
                if (Viz() is { } v) v.IsActive = true;
                await Task.Delay(1800);
                Snap(window, Path.Combine(outDir, file + ".png"));
                L(file);
            }
            vm.VisualizerStyle = VisualizerStyle.MirroredBlocks;

            // ---- Library: Electro and Alternative ----
            vm.CurrentPage = AppPage.Library;
            vm.Library.FavoritesOnly = false;
            vm.Library.IsGridView = true;
            vm.Library.ArtSize = "Large";
            vm.Library.SetSort(SortField.ArtistName);
            await Task.Delay(3000);
            foreach (var g in new[] { "Electro", "Alternative" })
                if (vm.Library.GenreChips.FirstOrDefault(c => c.Key.Equals(g, StringComparison.OrdinalIgnoreCase)) is { } chip) chip.IsSelected = true;
            await Task.Delay(3500);
            Snap(window, Path.Combine(outDir, "library-shelves.png"));
            L("library-shelves");

            vm.Library.IsGridView = false;
            await Task.Delay(2500);
            Snap(window, Path.Combine(outDir, "library-songs.png"));
            L("library-songs");
            vm.Library.IsGridView = true;
            await Task.Delay(1500);

            // Album spotlight and artist page from the Electro / Alternative shelves.
            var album = vm.Library.Albums.FirstOrDefault(a => a.Tracks.Count >= 8) ?? vm.Library.Albums.FirstOrDefault();
            if (album != null)
            {
                vm.Library.SelectedAlbum = album;
                await Task.Delay(3000);
                Snap(window, Path.Combine(outDir, "album-spotlight.png"));
                L("album-spotlight: " + album.Title);
                vm.Library.SelectedAlbum = null;
                await Task.Delay(800);
                vm.Library.OpenArtist(album.Artist);
                await Task.Delay(3000);
                Snap(window, Path.Combine(outDir, "artist.png"));
                L("artist: " + album.Artist);
                vm.Library.CloseArtistCommand?.Execute(null);
                vm.Library.SelectedArtist = null;
            }

            // ---- Search ----
            vm.CurrentPage = AppPage.Search;
            vm.Search.Query = "";
            await Task.Delay(1200);
            Snap(window, Path.Combine(outDir, "search.png"));
            vm.Search.Query = "bladee";
            await Task.Delay(3500);
            vm.Search.ShowSuggestions = vm.Search.Suggestions.Count > 0;
            await Task.Delay(1000);
            Snap(window, Path.Combine(outDir, "search-suggestions.png"));
            if (vm.Search.Suggestions.FirstOrDefault(x => x.IsArtist) is { } pick)
            {
                await vm.Search.PickAsync(pick);
                await Task.Delay(4000);
                Snap(window, Path.Combine(outDir, "search-results.png"));
            }
            L("search");

            // ---- Settings (the Spotify account name is blurred) ----
            vm.CurrentPage = AppPage.Settings;
            await Task.Delay(2500);
            SnapBlurringName(window, spotify.UserName, Path.Combine(outDir, "settings.png"));
            L("settings");

            // ---- Mini player: art, then the LCD bar (with controls) ----
            vm.CurrentPage = AppPage.NowPlaying;
            Flow.Views.MiniPlayerWindow.ProbeMode = true;
            Flow.Views.MiniPlayerWindow.Toggle(vm, settings);
            var mini = Flow.Views.MiniPlayerWindow.Current!;
            mini.ProbeSetPanel(true);
            mini.ProbeSetSize(300, 372);
            await Task.Delay(1500);
            mini.ProbeShowControls(true);
            await Task.Delay(800);
            Snap(mini, Path.Combine(outDir, "mini-player.png"));
            mini.ProbeShowControls(false);
            mini.ProbeSetSize(760, 70);
            await Task.Delay(1000);
            Snap(mini, Path.Combine(outDir, "mini-player-bar.png"));
            mini.ProbeShowControls(true);
            await Task.Delay(800);
            Snap(mini, Path.Combine(outDir, "mini-player-bar-controls.png"));
            mini.Close();
            L("mini player");

            feed.Stop();
            L("DONE");
        }
        catch (Exception ex) { L("FAILED: " + ex); }
        Environment.Exit(0);
    }

    /// <summary>
    /// GitHub's social preview (1280x640): the app icon, name and tagline on brushed metal, with the Brushed Metal
    /// screenshot floating on the right.
    /// </summary>
    private static void SocialCard(string screenshot, Brush metal, string file)
    {
        const int W = 1280, H = 640;
        var shot = new BitmapImage();
        shot.BeginInit();
        shot.CacheOption = BitmapCacheOption.OnLoad;
        shot.UriSource = new Uri(screenshot);
        shot.EndInit();
        var icon = new BitmapImage(new Uri("pack://application:,,,/Assets/Flow.png"));

        var root = new Grid { Width = W, Height = H, Background = metal, ClipToBounds = true };
        // Light from the top, shade toward the bottom, like the app's window.
        root.Children.Add(new Border
        {
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0), new(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.35),
                new(Color.FromArgb(0x00, 0, 0, 0), 0.6), new(Color.FromArgb(0x45, 0, 0, 0), 1),
            }, 90),
        });

        // The app, floating on the right and running off the edge.
        var shotW = 900.0;
        var frame = new Border
        {
            Width = shotW, Height = shotW * 1000 / 1600, CornerRadius = new CornerRadius(14), ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(500, 64, 0, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0x5A, 0x5C, 0x62)), BorderThickness = new Thickness(1),
            Background = new ImageBrush(shot) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 14, Direction = 270, Opacity = 0.5 },
        };
        root.Children.Add(frame);

        // Name, tagline and highlights on the left.
        var dark = new SolidColorBrush(Color.FromRgb(0x18, 0x19, 0x1C));
        var mid = new SolidColorBrush(Color.FromRgb(0x3C, 0x3F, 0x45));
        var text = new StackPanel { Margin = new Thickness(64, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 428, HorizontalAlignment = HorizontalAlignment.Left };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Image
        {
            Source = icon, Width = 84, Height = 84, Margin = new Thickness(0, 0, 18, 0),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 4, Direction = 270, Opacity = 0.4 },
        });
        title.Children.Add(new TextBlock
        {
            Text = "Flow", FontSize = 84, FontWeight = FontWeights.SemiBold, Foreground = dark, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        });
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            Text = "The minimal and beautiful music player for Windows 11", FontSize = 28, Foreground = dark, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 18, 0, 0), FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        });
        text.Children.Add(new TextBlock
        {
            Text = "Play almost any audio file and your Spotify library and playlists. Organize your albums and arrange tracklists.",
            FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = dark, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 16, 0, 0),
            LineHeight = 25, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        });
        text.Children.Add(new TextBlock
        {
            Text = "A modern take on the classic mid-2000s iTunes feel: big album art, a living visualizer, an LCD-style display, and six looks from see-through to dark brushed metal.",
            FontSize = 17, Foreground = mid, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 16, 0, 0), LineHeight = 25,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        });
        text.Children.Add(new TextBlock
        {
            Text = "github.com/leansalesman/Flow", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x52, 0xA8)),
            Margin = new Thickness(4, 26, 0, 0), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        });
        root.Children.Add(text);

        root.Measure(new Size(W, H));
        root.Arrange(new Rect(0, 0, W, H));
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t && (c is not UIElement u || u.IsVisible)) return t;
            if (Find<T>(c) is { } found) return found;
        }
        return null;
    }

    /// <summary>Snapshot with the Spotify account name blurred wherever it's shown ("Connected as …").</summary>
    private static void SnapBlurringName(Window w, string? name, string file)
    {
        var root = (FrameworkElement)w.Content;
        int width = (int)root.ActualWidth, height = (int)root.ActualHeight;
        var shot = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        shot.Render(root);
        var regions = new List<Rect>();
        if (!string.IsNullOrEmpty(name))
            foreach (var tb in All<TextBlock>(root).Where(t => t.IsVisible && t.Text.Contains(" as " + name)))
            {
                var dpi = VisualTreeHelper.GetDpi(tb).PixelsPerDip;
                var face = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
                double Measure(string s) => new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                                              face, tb.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
                var prefix = tb.Text[..(tb.Text.IndexOf(" as " + name, StringComparison.Ordinal) + 4)];
                var origin = tb.TransformToAncestor(root).Transform(new Point(0, 0));
                regions.Add(new Rect(origin.X + Measure(prefix) - 3, origin.Y - 2, Measure(name) + 6, tb.ActualHeight + 4));
            }
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x28)), null, new Rect(0, 0, width, height));
            dc.DrawImage(shot, new Rect(0, 0, width, height));
        }
        var composed = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        composed.Render(visual);
        foreach (var r in regions)
        {
            var px = new Int32Rect((int)r.X, (int)r.Y, (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height));
            var crop = new CroppedBitmap(composed, px);
            var img = new Image { Source = crop, Width = px.Width, Height = px.Height, Effect = new BlurEffect { Radius = 9 } };
            var host = new Grid { Width = px.Width, Height = px.Height, ClipToBounds = true };
            host.Children.Add(img);
            host.Measure(new Size(px.Width, px.Height));
            host.Arrange(new Rect(0, 0, px.Width, px.Height));
            var blurred = new RenderTargetBitmap(px.Width, px.Height, 96, 96, PixelFormats.Pbgra32);
            blurred.Render(host);
            var v2 = new DrawingVisual();
            using (var dc = v2.RenderOpen()) dc.DrawImage(blurred, new Rect(px.X, px.Y, px.Width, px.Height));
            composed.Render(v2);
        }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(composed));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in All<T>(c)) yield return x;
        }
    }

    /// <summary>Kick, bass and chords at 48 kHz stereo: enough to light up every band of the visualizer.</summary>
    private sealed class SyntheticMusic
    {
        private double _t;
        private readonly Random _rng = new(5);
        public float[] Next(int count)
        {
            var buf = new float[count];
            for (int i = 0; i < count; i += 2)
            {
                _t += 1.0 / 48000;
                double beat = _t * 2.05 % 1.0;
                double kick = beat < 0.14 ? Math.Sin(2 * Math.PI * (48 + 90 * (0.14 - beat)) * _t) * (1 - beat / 0.14) : 0;
                double wobble = 0.5 + 0.5 * Math.Sin(_t * 1.7);
                double s = 0.6 * kick + 0.22 * Math.Sin(2 * Math.PI * 73 * _t) + 0.14 * wobble * Math.Sin(2 * Math.PI * 294 * _t)
                           + 0.1 * Math.Sin(2 * Math.PI * 440 * _t) + 0.07 * Math.Sin(2 * Math.PI * 1320 * _t) * (1 - wobble)
                           + 0.05 * Math.Sin(2 * Math.PI * 3300 * _t) + 0.05 * (_rng.NextDouble() * 2 - 1);
                buf[i] = buf[i + 1] = (float)s;
            }
            return buf;
        }
    }
}
#endif
