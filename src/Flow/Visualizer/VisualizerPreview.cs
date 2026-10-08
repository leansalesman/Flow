#if DEBUG
// Developer aid, left out of release builds.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flow.Visualizer;

/// <summary>
/// Developer aid: `Flow.exe --render-visualizers &lt;folder&gt;` renders every style offscreen
/// (fed a synthetic music signal) to PNG files and exits. No window is shown.
/// </summary>
public static class VisualizerPreview
{
    public static void RenderAll(string outDir)
    {
        Directory.CreateDirectory(outDir);
        const double W = 1200, H = 560, Art = 300;
        var analyzer = new SpectrumAnalyzer { SampleRate = 48000 };
        var rng = new Random(3);
        double phase = 0;

        foreach (var style in Enum.GetValues<VisualizerStyle>())
        {
            var art = new Border { Width = Art, Height = Art, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)) };
            var viz = new MusicVisualizer
            {
                Analyzer = analyzer,
                IsActive = true,
                ArtSize = Art,
                BlockColor = Color.FromRgb(0x9A, 0xA4, 0xB8),
                PeakColor = Color.FromRgb(0xE6, 0xEC, 0xF5),
                VisualStyle = style,
                ArtElement = art,
            };
            var root = new Grid { Width = W, Height = H, Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20)) };
            root.Children.Add(viz);
            root.Children.Add(art);
            root.Measure(new Size(W, H));
            root.Arrange(new Rect(0, 0, W, H));
            root.UpdateLayout();

            // ~1.5 s of "music": kick drum + bass + chords + hats.
            var buf = new float[1600];
            for (int frame = 0; frame < 90; frame++)
            {
                for (int i = 0; i < buf.Length; i += 2)
                {
                    phase += 1.0 / 48000;
                    double beat = phase * 2.1 % 1.0;
                    double kick = beat < 0.12 ? Math.Sin(2 * Math.PI * (50 + 80 * (0.12 - beat)) * phase) * (1 - beat / 0.12) : 0;
                    double s = 0.55 * kick + 0.25 * Math.Sin(2 * Math.PI * 82 * phase)
                               + 0.12 * Math.Sin(2 * Math.PI * 330 * phase) + 0.1 * Math.Sin(2 * Math.PI * 523 * phase)
                               + 0.05 * Math.Sin(2 * Math.PI * 2200 * phase) + 0.04 * (rng.NextDouble() * 2 - 1);
                    buf[i] = buf[i + 1] = (float)s;
                }
                analyzer.Write(buf, 0, buf.Length);
                viz.Tick(1f / 60f);
            }

            viz.InvalidateVisual();
            root.UpdateLayout(); // re-run OnRender now that the frames have been drawn
            var rtb = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(outDir, $"{(int)style:00}_{style}.png"));
            enc.Save(fs);
        }
    }
}
#endif
