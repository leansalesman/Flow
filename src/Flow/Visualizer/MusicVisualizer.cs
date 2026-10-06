using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;

namespace Flow.Visualizer;

public enum VisualizerStyle
{
    MirroredBlocks, RadialRing, MirrorMountains, SpectrumBars, Oscilloscope,
    Spectrogram, BlockRain, Particles, BassHalo, Off,
}

/// <summary>
/// Music visualizer with several switchable styles. Pixel styles render into a per-pixel-alpha bitmap
/// (cheap, crisp blocks); smooth styles draw vector geometry. Everything keeps a transparent background
/// so the translucent window shows through.
/// </summary>
public sealed class MusicVisualizer : FrameworkElement
{
    public static string DisplayName(VisualizerStyle s) => s switch
    {
        VisualizerStyle.MirroredBlocks => "Mirrored Blocks",
        VisualizerStyle.RadialRing => "Radial Ring",
        VisualizerStyle.MirrorMountains => "Mirror Mountains",
        VisualizerStyle.SpectrumBars => "Spectrum Bars",
        VisualizerStyle.Oscilloscope => "Oscilloscope",
        VisualizerStyle.Spectrogram => "Spectrogram",
        VisualizerStyle.BlockRain => "Block Rain",
        VisualizerStyle.Particles => "Particle Field",
        VisualizerStyle.BassHalo => "Bass Halo",
        _ => "Visualizer Off",
    };

    // ---- Dependency properties ----------------------------------------------------------------

    public static readonly DependencyProperty ArtSizeProperty = DependencyProperty.Register(
        nameof(ArtSize), typeof(double), typeof(MusicVisualizer),
        new FrameworkPropertyMetadata(300.0, (d, _) => ((MusicVisualizer)d)._layoutDirty = true));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(MusicVisualizer), new PropertyMetadata(false));

    public static readonly DependencyProperty BlockColorProperty = DependencyProperty.Register(
        nameof(BlockColor), typeof(Color), typeof(MusicVisualizer),
        new PropertyMetadata(Color.FromRgb(0x60, 0xCD, 0xFF), (d, _) => ((MusicVisualizer)d).OnColorsChanged()));

    public static readonly DependencyProperty PeakColorProperty = DependencyProperty.Register(
        nameof(PeakColor), typeof(Color), typeof(MusicVisualizer),
        new PropertyMetadata(Colors.White, (d, _) => ((MusicVisualizer)d).OnColorsChanged()));

    public static readonly DependencyProperty VisualStyleProperty = DependencyProperty.Register(
        nameof(VisualStyle), typeof(VisualizerStyle), typeof(MusicVisualizer),
        new PropertyMetadata(VisualizerStyle.MirroredBlocks, (d, e) => ((MusicVisualizer)d).OnStyleChanged((VisualizerStyle)e.OldValue)));

    /// <summary>The album art element; the Bass Halo style gently scales it with the bass.</summary>
    public static readonly DependencyProperty ArtElementProperty = DependencyProperty.Register(
        nameof(ArtElement), typeof(UIElement), typeof(MusicVisualizer), new PropertyMetadata(null));

    public double ArtSize { get => (double)GetValue(ArtSizeProperty); set => SetValue(ArtSizeProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Color BlockColor { get => (Color)GetValue(BlockColorProperty); set => SetValue(BlockColorProperty, value); }
    public Color PeakColor { get => (Color)GetValue(PeakColorProperty); set => SetValue(PeakColorProperty, value); }
    public VisualizerStyle VisualStyle { get => (VisualizerStyle)GetValue(VisualStyleProperty); set => SetValue(VisualStyleProperty, value); }
    public UIElement? ArtElement { get => (UIElement?)GetValue(ArtElementProperty); set => SetValue(ArtElementProperty, value); }

    public SpectrumAnalyzer? Analyzer { get; set; }

    // ---- Shared state ---------------------------------------------------------------------------

    private WriteableBitmap? _bmp;
    private bool _layoutDirty = true, _forceRedraw = true, _idleDrawn, _subscribed;
    private int _pw, _ph, _block, _gap, _step, _cx, _cy, _artPx, _margin;
    private double _dpi = 1;
    private float[] _mask = Array.Empty<float>();
    private float[] _raw = Array.Empty<float>(), _level = Array.Empty<float>(), _peak = Array.Empty<float>(), _hold = Array.Empty<float>();
    private float _bass, _mid, _treble;
    private TimeSpan _lastFrame;
    private readonly Random _rng = new(17);

    private static bool IsVector(VisualizerStyle s) =>
        s is VisualizerStyle.Oscilloscope or VisualizerStyle.MirrorMountains or VisualizerStyle.BassHalo;

    public MusicVisualizer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        IsVisibleChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => { if (_subscribed) { CompositionTarget.Rendering -= OnFrame; _subscribed = false; } };
    }

    private void UpdateSubscription()
    {
        bool want = IsVisible && IsLoaded;
        if (want && !_subscribed) { CompositionTarget.Rendering += OnFrame; _subscribed = true; _forceRedraw = true; }
        else if (!want && _subscribed)
        {
            CompositionTarget.Rendering -= OnFrame;
            _subscribed = false;
            // Hidden (other page / minimized): release the drawing surface; it's rebuilt when shown again.
            _bmp = null;
            _spec = Array.Empty<float>();
            _specCols = _specRowsTotal = 0;
            _drops.Clear();
            _layoutDirty = true;
            InvalidateVisual();
        }
    }

    private void OnStyleChanged(VisualizerStyle old)
    {
        if (old is VisualizerStyle.BassHalo or VisualizerStyle.RadialRing) ResetArtScale();
        if (VisualStyle == VisualizerStyle.RadialRing) _forceRedraw = true;
        _forceRedraw = true;
        _idleDrawn = false;
        _drops.Clear();
        _particlesReady = false;
        _specRows = 0;
        Array.Clear(_level);
        Array.Clear(_peak);
        if (_bmp != null) ClearBitmap();
        InvalidateVisual();
    }

    private void OnColorsChanged()
    {
        _forceRedraw = true;
        _pens = null;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _layoutDirty = true;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _layoutDirty = true;
    }

    private void RebuildLayout()
    {
        _layoutDirty = false;
        _forceRedraw = true;
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _pw = Math.Max(1, (int)Math.Round(ActualWidth * _dpi));
        _ph = Math.Max(1, (int)Math.Round(ActualHeight * _dpi));
        _artPx = (int)Math.Round(ArtSize * _dpi);
        _block = Math.Max(4, (int)Math.Round(_artPx / 24.0));
        _gap = Math.Max(2, (int)Math.Round(_block * 0.42));
        _step = _block + _gap;
        _margin = (int)Math.Round(26 * _dpi);
        _cx = _pw / 2;
        _cy = _ph / 2;

        int b = _block;
        _mask = new float[b * b];
        float r = b * 0.28f;
        for (int y = 0; y < b; y++)
            for (int x = 0; x < b; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float qx = Math.Clamp(px, r, b - r), qy = Math.Clamp(py, r, b - r);
                float d = MathF.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
                _mask[y * b + x] = Math.Clamp(r - d + 0.5f, 0f, 1f);
            }

        BuildDotSprites();
        _bmp = new WriteableBitmap(_pw, _ph, 96 * _dpi, 96 * _dpi, PixelFormats.Pbgra32, null);
        _particlesReady = false;
        _specRows = 0;
        _drops.Clear();
        InvalidateVisual();
    }

    private void EnsureBands(int n)
    {
        if (_raw.Length == n) return;
        _raw = new float[n];
        _level = new float[n];
        _peak = new float[n];
        _hold = new float[n];
    }

    private int BandCount(VisualizerStyle s) => s switch
    {
        VisualizerStyle.MirroredBlocks => Math.Max(1, MirroredCols()),
        VisualizerStyle.RadialRing => 24,
        VisualizerStyle.MirrorMountains => 40,
        VisualizerStyle.SpectrumBars => Math.Max(8, BarCount()),
        VisualizerStyle.Spectrogram => 96,
        VisualizerStyle.BlockRain => 48,
        VisualizerStyle.Particles => 24,
        _ => 16,
    };

    // ---- Frame loop ---------------------------------------------------------------------------

    private void OnFrame(object? sender, EventArgs e)
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;
        var now = e is RenderingEventArgs re ? re.RenderingTime : TimeSpan.FromTicks(Environment.TickCount64 * 10000);
        if (now == _lastFrame) return;
        float dt = (float)Math.Clamp((now - _lastFrame).TotalSeconds, 0.001, 0.1);
        _lastFrame = now;
        Tick(dt);
    }

    /// <summary>Advances one frame. Public so the offscreen preview renderer can drive it.</summary>
    public void Tick(float dt)
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;

        if (_layoutDirty) RebuildLayout();
        if (_bmp == null) return;

        var style = VisualStyle;
        if (style == VisualizerStyle.Off)
        {
            if (!_idleDrawn || _forceRedraw) { ClearBitmap(); InvalidateVisual(); _idleDrawn = true; _forceRedraw = false; }
            return;
        }

        int n = BandCount(style);
        EnsureBands(n);
        bool active = IsActive && Analyzer != null;
        if (active) Analyzer!.Compute(_raw, n);
        else Array.Clear(_raw);

        bool any = false;
        for (int i = 0; i < n; i++)
        {
            float target = _raw[i];
            float lv = _level[i];
            lv = target > lv ? lv + (target - lv) * 0.65f : Math.Max(target, lv - 1.7f * dt);
            _level[i] = lv;
            if (lv >= _peak[i]) { _peak[i] = lv; _hold[i] = 0.32f; }
            else if (_hold[i] > 0) _hold[i] -= dt;
            else _peak[i] = Math.Max(0, _peak[i] - 0.85f * dt);
            if (lv > 0.002f || _peak[i] > 0.002f) any = true;
        }
        Bands(out float bass, out float mid, out float treble);
        _bass = Smooth(_bass, bass, dt);
        _mid = Smooth(_mid, mid, dt);
        _treble = Smooth(_treble, treble, dt);

        // Styles with their own motion keep animating briefly after the music stops.
        bool animating = style switch
        {
            VisualizerStyle.Particles => true,
            VisualizerStyle.BlockRain => _drops.Count > 0,
            VisualizerStyle.Oscilloscope => _waveEnergy > 0.002f,
            VisualizerStyle.BassHalo => _bass > 0.002f || Math.Abs(_artScale - 1) > 0.001,
            VisualizerStyle.RadialRing => Math.Abs(_artScale - RingArtScale) > 0.001,
            _ => false,
        };
        if (!any && !animating && _idleDrawn && !_forceRedraw) return;

        switch (style)
        {
            case VisualizerStyle.MirroredBlocks: DrawMirroredBlocks(); break;
            case VisualizerStyle.RadialRing: SetArtScale(RingArtScale, dt); DrawRadialRing(); break;
            case VisualizerStyle.SpectrumBars: DrawSpectrumBars(); break;
            case VisualizerStyle.Spectrogram: DrawSpectrogram(dt, active); break;
            case VisualizerStyle.BlockRain: DrawBlockRain(dt, active); break;
            case VisualizerStyle.Particles: DrawParticles(dt); break;
            case VisualizerStyle.Oscilloscope: UpdateWave(active); InvalidateVisual(); break;
            case VisualizerStyle.MirrorMountains: InvalidateVisual(); break;
            case VisualizerStyle.BassHalo: SetArtScale(1 + _bass * 0.035, dt, fast: true); InvalidateVisual(); break;
        }
        _idleDrawn = !any && !animating;
        _forceRedraw = false;
    }

    private static float Smooth(float cur, float target, float dt) =>
        target > cur ? cur + (target - cur) * 0.5f : Math.Max(target, cur - 1.4f * dt);

    private void Bands(out float bass, out float mid, out float treble)
    {
        int n = _level.Length;
        int a = Math.Max(1, n / 6), b = Math.Max(a + 1, n / 2);
        bass = Avg(0, a); mid = Avg(a, b); treble = Avg(b, n);
        float Avg(int from, int to)
        {
            if (to <= from) return 0;
            float s = 0, m = 0;
            for (int i = from; i < to; i++) { s += _level[i]; m = Math.Max(m, _level[i]); }
            return (s / (to - from)) * 0.5f + m * 0.5f;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var style = VisualStyle;
        if (IsVector(style))
        {
            switch (style)
            {
                case VisualizerStyle.Oscilloscope: RenderOscilloscope(dc); break;
                case VisualizerStyle.MirrorMountains: RenderMountains(dc); break;
                case VisualizerStyle.BassHalo: RenderHalo(dc); break;
            }
            return;
        }
        if (_bmp != null) dc.DrawImage(_bmp, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    // ---- Bitmap helpers -------------------------------------------------------------------------

    private unsafe void ClearBitmap()
    {
        if (_bmp == null) return;
        _bmp.Lock();
        new Span<byte>((void*)_bmp.BackBuffer, _bmp.BackBufferStride * _ph).Clear();
        _bmp.AddDirtyRect(new Int32Rect(0, 0, _pw, _ph));
        _bmp.Unlock();
    }

    private unsafe byte* BeginDraw(bool clear)
    {
        _bmp!.Lock();
        var p = (byte*)_bmp.BackBuffer;
        if (clear) new Span<byte>(p, _bmp.BackBufferStride * _ph).Clear();
        return p;
    }

    private void EndDraw()
    {
        _bmp!.AddDirtyRect(new Int32Rect(0, 0, _pw, _ph));
        _bmp.Unlock();
    }

    private static Color Lerp(Color a, Color b, float t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static uint Pack(Color c, float a)
    {
        a = Math.Clamp(a, 0f, 1f);
        return ((uint)(a * 255f) << 24) | ((uint)(c.R * a) << 16) | ((uint)(c.G * a) << 8) | (uint)(c.B * a);
    }

    /// <summary>Rounded block sprite at (x0,y0), overwriting.</summary>
    private unsafe void Blit(byte* basePtr, int stride, int x0, int y0, Color c, float alpha)
    {
        int b = _block;
        if (x0 < 0 || y0 < 0 || x0 + b > _pw || y0 + b > _ph || alpha <= 0.003f) return;
        for (int y = 0; y < b; y++)
        {
            uint* row = (uint*)(basePtr + (y0 + y) * stride) + x0;
            int m = y * b;
            for (int x = 0; x < b; x++)
            {
                float a = _mask[m + x] * alpha;
                if (a <= 0.002f) continue;
                row[x] = Pack(c, a);
            }
        }
    }

    private unsafe void FillRect(byte* basePtr, int stride, int x0, int y0, int w, int h, Color c, float alpha)
    {
        int x1 = Math.Min(_pw, x0 + w), y1 = Math.Min(_ph, y0 + h);
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        if (x1 <= x0 || y1 <= y0 || alpha <= 0.003f) return;
        uint v = Pack(c, alpha);
        for (int y = y0; y < y1; y++)
        {
            uint* row = (uint*)(basePtr + y * stride);
            for (int x = x0; x < x1; x++) row[x] = v;
        }
    }

    // ---- 1. Mirrored blocks ---------------------------------------------------------------------

    private int MirroredCols()
    {
        int side = (_pw - _artPx) / 2 - _margin - (int)Math.Round(14 * _dpi);
        return Math.Clamp((side + _gap) / Math.Max(1, _step), 0, 40);
    }

    private int HalfRows() => Math.Clamp((_ph / 2 - _gap / 2) / Math.Max(1, _step), 1, 30);

    private unsafe void DrawMirroredBlocks()
    {
        int cols = MirroredCols(), rows = HalfRows();
        if (cols == 0 || _level.Length < cols) { ClearBitmap(); return; }
        var basePtr = BeginDraw(true);
        int stride = _bmp!.BackBufferStride;
        Color c0 = BlockColor, c1 = PeakColor;
        int artHalf = _artPx / 2;
        for (int col = 0; col < cols; col++)
        {
            int lit = (int)Math.Round(_level[col] * rows);
            int peakRow = (int)Math.Round(_peak[col] * rows) - 1;
            float colFade = 1f - 0.45f * col / Math.Max(1, cols);
            int xRight = _cx + artHalf + _margin + col * _step;
            int xLeft = _cx - artHalf - _margin - _block - col * _step;
            for (int k = 0; k < rows; k++)
            {
                if (!BlockState(k, lit, peakRow, rows, colFade, c0, c1, out var c, out float alpha)) continue;
                int yUp = _cy - _gap / 2 - _block - k * _step;
                int yDown = _cy + (_gap + 1) / 2 + k * _step;
                Blit(basePtr, stride, xLeft, yUp, c, alpha);
                Blit(basePtr, stride, xRight, yUp, c, alpha);
                Blit(basePtr, stride, xLeft, yDown, c, alpha * 0.45f);
                Blit(basePtr, stride, xRight, yDown, c, alpha * 0.45f);
            }
        }
        EndDraw();
    }

    /// <summary>Shared look for stacked block columns: lit blocks fade toward the tip, a bright peak, dim baseline.</summary>
    private static bool BlockState(int k, int lit, int peakRow, int rows, float fade, Color c0, Color c1, out Color c, out float alpha)
    {
        float t = (float)k / rows;
        if (k < lit) { alpha = (1f - 0.5f * t) * fade; c = Lerp(c0, c1, t * 0.55f); return true; }
        if (k == peakRow && peakRow >= lit && peakRow > 0) { alpha = 0.95f * fade; c = c1; return true; }
        if (k == 0) { alpha = 0.16f * fade; c = c0; return true; }
        c = c0; alpha = 0; return false;
    }

    // ---- 2. Radial ring -------------------------------------------------------------------------

    private const double RingArtScale = 0.74;

    private unsafe void DrawRadialRing()
    {
        int bands = 24; // per quadrant
        if (_level.Length < bands) return;
        // The art shrinks a little in this mode so the ring has room to breathe.
        double artHalf = _artPx * RingArtScale / 2;
        double r0 = artHalf * 1.36 + _gap * 2;
        double room = Math.Max(_pw, _ph) / 2.0 - r0 - _block;
        int rows = Math.Clamp((int)(room / _step), 4, 13);
        var basePtr = BeginDraw(true);
        int stride = _bmp!.BackBufferStride;
        Color c0 = BlockColor, c1 = PeakColor;
        int half = _block / 2;
        for (int b = 0; b < bands; b++)
        {
            int lit = (int)Math.Round(_level[b] * rows);
            int peakRow = (int)Math.Round(_peak[b] * rows) - 1;
            // Bass points left/right (most room), treble sweeps toward top and bottom; mirrored into all four quadrants.
            double a = Math.PI / 2 * (b + 0.5) / bands;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            for (int k = 0; k < rows; k++)
            {
                if (!BlockState(k, lit, peakRow, rows, 1f, c0, c1, out var c, out float alpha)) continue;
                double r = r0 + k * _step;
                int dx = (int)(r * cos), dy = (int)(r * sin);
                Blit(basePtr, stride, _cx + dx - half, _cy - dy - half, c, alpha);
                Blit(basePtr, stride, _cx - dx - half, _cy - dy - half, c, alpha);
                Blit(basePtr, stride, _cx + dx - half, _cy + dy - half, c, alpha * 0.8f);
                Blit(basePtr, stride, _cx - dx - half, _cy + dy - half, c, alpha * 0.8f);
            }
        }
        EndDraw();
    }
    // ---- 3. Spectrum bars -----------------------------------------------------------------------

    private int BarW => Math.Max(3, (int)Math.Round(6 * _dpi));
    private int BarGap => Math.Max(2, (int)Math.Round(3 * _dpi));
    private int BarCount() => Math.Clamp((_pw - 2 * _margin + BarGap) / (BarW + BarGap), 8, 160);

    private unsafe void DrawSpectrumBars()
    {
        int n = BarCount();
        if (_level.Length < n) return;
        var basePtr = BeginDraw(true);
        int stride = _bmp!.BackBufferStride;
        Color c0 = BlockColor, c1 = PeakColor;
        int w = BarW, gap = BarGap;
        int total = n * (w + gap) - gap;
        int x0 = (_pw - total) / 2;
        int baseY = _ph - (int)Math.Round(6 * _dpi);
        double maxH = _ph * 0.42;
        int cap = Math.Max(2, (int)Math.Round(2 * _dpi));
        for (int i = 0; i < n; i++)
        {
            float edge = 1f - 0.35f * Math.Abs(i - n / 2f) / (n / 2f);
            int h = (int)(_level[i] * maxH);
            int x = x0 + i * (w + gap);
            // Base glow so the shape reads when quiet.
            FillRect(basePtr, stride, x, baseY - cap, w, cap, c0, 0.18f * edge);
            for (int y = 0; y < h; y += 2)
            {
                float t = (float)y / (float)maxH;
                FillRect(basePtr, stride, x, baseY - y - 2, w, 2, Lerp(c0, c1, t), (0.9f - 0.35f * t) * edge);
            }
            int py = baseY - (int)(_peak[i] * maxH) - cap * 3;
            if (_peak[i] > 0.02f) FillRect(basePtr, stride, x, py, w, cap, c1, 0.95f * edge);
        }
        EndDraw();
    }

    // ---- 4. Spectrogram -------------------------------------------------------------------------

    private float[] _spec = Array.Empty<float>();
    private int _specCols, _specRowsTotal, _specRows, _specHead;
    private float _specAcc;

    private unsafe void DrawSpectrogram(float dt, bool active)
    {
        int cell = Math.Max(4, (int)Math.Round(5 * _dpi));
        int cols = Math.Max(1, _pw / cell), rows = Math.Max(1, _ph / cell);
        if (cols != _specCols || rows != _specRowsTotal)
        {
            _specCols = cols; _specRowsTotal = rows;
            _spec = new float[cols * rows];
            _specRows = 0; _specHead = 0;
        }
        if (active)
        {
            _specAcc += dt;
            const float rowInterval = 1f / 22f;
            while (_specAcc >= rowInterval)
            {
                _specAcc -= rowInterval;
                // Newest row is written at _specHead; it is drawn at the bottom and older rows move up.
                int baseIdx = _specHead * cols;
                int n = _level.Length;
                for (int c = 0; c < cols; c++)
                {
                    float f = (float)c / cols * (n - 1);
                    int i = (int)f;
                    float frac = f - i;
                    float v = _level[i] * (1 - frac) + _level[Math.Min(n - 1, i + 1)] * frac;
                    _spec[baseIdx + c] = v;
                }
                _specHead = (_specHead + 1) % rows;
                _specRows = Math.Min(rows, _specRows + 1);
            }
        }

        var basePtr = BeginDraw(true);
        int stride = _bmp!.BackBufferStride;
        Color c0 = BlockColor, c1 = PeakColor;
        int ox = (_pw - cols * cell) / 2;
        for (int age = 0; age < _specRows; age++)
        {
            int r = (_specHead - 1 - age + rows * 2) % rows;
            int y = _ph - (age + 1) * cell;
            float fade = 1f - (float)age / rows * 0.8f;
            for (int c = 0; c < cols; c++)
            {
                float v = _spec[r * cols + c];
                if (v < 0.05f) continue;
                Color col = v < 0.6f ? Lerp(c0, c1, v / 0.6f * 0.6f) : Lerp(c1, Colors.White, (v - 0.6f) / 0.4f * 0.7f);
                FillRect(basePtr, stride, ox + c * cell, y, cell - 1, cell - 1, col, Math.Min(1f, v * 1.25f) * fade);
            }
        }
        EndDraw();
    }

    // ---- 5. Block rain --------------------------------------------------------------------------

    private struct RainDrop { public int Col; public float Y, Speed, Alpha; }
    private readonly List<RainDrop> _drops = new();

    private unsafe void DrawBlockRain(float dt, bool active)
    {
        int cols = Math.Max(1, _pw / _step);
        int rows = Math.Max(2, _ph / _step);
        int ox = (_pw - cols * _step + _gap) / 2;
        int floorY = (rows - 1) * _step;
        int n = _level.Length;
        float ColLevel(int c) => _level[Math.Min(n - 1, (int)((float)c / cols * n))];

        if (active)
            for (int c = 0; c < cols; c++)
            {
                float lv = ColLevel(c);
                if (_rng.NextDouble() < lv * lv * dt * 7 && _drops.Count < 700)
                    _drops.Add(new RainDrop { Col = c, Y = -_step, Speed = (float)((160 + 460 * lv) * _dpi), Alpha = 0.45f + 0.55f * lv });
            }

        for (int i = _drops.Count - 1; i >= 0; i--)
        {
            var d = _drops[i];
            d.Y += d.Speed * dt;
            if (d.Y >= floorY) _drops.RemoveAt(i);
            else _drops[i] = d;
        }

        var basePtr = BeginDraw(true);
        int stride = _bmp!.BackBufferStride;
        Color c0 = BlockColor, c1 = PeakColor;
        foreach (var d in _drops)
        {
            int row = (int)(d.Y / _step);
            int x = ox + d.Col * _step;
            Blit(basePtr, stride, x, row * _step, c1, d.Alpha);
            for (int t = 1; t <= 3; t++)
                if (row - t >= 0) Blit(basePtr, stride, x, (row - t) * _step, c0, d.Alpha * (0.5f / t));
        }
        // The floor glows with the level of its column.
        for (int c = 0; c < cols; c++)
        {
            float lv = ColLevel(c);
            int x = ox + c * _step;
            Blit(basePtr, stride, x, floorY, Lerp(c0, c1, lv), 0.14f + 0.8f * lv);
            if (lv > 0.55f) Blit(basePtr, stride, x, floorY - _step, c0, (lv - 0.55f) * 1.6f);
        }
        EndDraw();
    }

    // ---- 6. Particle field ----------------------------------------------------------------------

    private struct Particle { public float X, Y, Vx, Vy, Size, Phase, Tint; }
    private readonly Particle[] _particles = new Particle[170];
    private bool _particlesReady;
    private float[][] _dots = Array.Empty<float[]>();

    private void BuildDotSprites()
    {
        int maxR = Math.Max(4, (int)Math.Round(12 * _dpi));
        _dots = new float[maxR + 1][];
        for (int r = 1; r <= maxR; r++)
        {
            int size = r * 2 + 1;
            var s = new float[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x - r, dy = y - r;
                    float d = MathF.Sqrt(dx * dx + dy * dy) / r;
                    s[y * size + x] = d >= 1 ? 0 : MathF.Pow(1 - d, 1.15f);
                }
            _dots[r] = s;
        }
    }

    private void InitParticles()
    {
        for (int i = 0; i < _particles.Length; i++)
        {
            double ang = _rng.NextDouble() * Math.PI * 2;
            float speed = (float)(6 + _rng.NextDouble() * 18) * (float)_dpi;
            _particles[i] = new Particle
            {
                X = (float)(_rng.NextDouble() * _pw),
                Y = (float)(_rng.NextDouble() * _ph),
                Vx = (float)Math.Cos(ang) * speed,
                Vy = (float)Math.Sin(ang) * speed,
                Size = (float)(0.25 + Math.Pow(_rng.NextDouble(), 2.2) * 0.75),
                Phase = (float)(_rng.NextDouble() * Math.PI * 2),
                Tint = (float)_rng.NextDouble(),
            };
        }
        _particlesReady = true;
    }

    private unsafe void DrawParticles(float dt)
    {
        if (!_particlesReady) InitParticles();
        float push = _bass * 260f * (float)_dpi;
        int maxR = _dots.Length - 1;
        var basePtr = BeginDraw(true);
        int stride = _bmp!.BackBufferStride;
        Color c0 = BlockColor, c1 = PeakColor;
        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            float dx = p.X - _cx, dy = p.Y - _cy;
            float len = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
            p.X += (p.Vx + dx / len * push * p.Size) * dt;
            p.Y += (p.Vy + dy / len * push * p.Size) * dt;
            p.Phase += dt * (1.5f + p.Tint * 2);
            if (p.X < -20) p.X += _pw + 40; else if (p.X > _pw + 20) p.X -= _pw + 40;
            if (p.Y < -20) p.Y += _ph + 40; else if (p.Y > _ph + 20) p.Y -= _ph + 40;

            float twinkle = 0.5f + 0.5f * MathF.Sin(p.Phase);
            float alpha = 0.32f + 0.3f * twinkle + _bass * 0.6f * p.Size + _treble * 0.25f * twinkle;
            int r = Math.Clamp((int)Math.Round((3 + p.Size * 9 * (1 + _bass * 0.8f)) * _dpi), 1, maxR);
            StampDot(basePtr, stride, (int)p.X, (int)p.Y, r, Lerp(c0, c1, p.Tint * 0.7f + _mid * 0.3f), alpha);
        }
        EndDraw();
    }

    /// <summary>Soft round dot, max-blended so overlaps glow instead of covering.</summary>
    private unsafe void StampDot(byte* basePtr, int stride, int cx, int cy, int r, Color c, float alpha)
    {
        var s = _dots[r];
        int size = r * 2 + 1;
        int x0 = cx - r, y0 = cy - r;
        for (int y = 0; y < size; y++)
        {
            int py = y0 + y;
            if (py < 0 || py >= _ph) continue;
            uint* row = (uint*)(basePtr + py * stride);
            for (int x = 0; x < size; x++)
            {
                int px = x0 + x;
                if (px < 0 || px >= _pw) continue;
                float a = s[y * size + x] * alpha;
                if (a <= 0.004f) continue;
                uint v = Pack(c, a);
                if ((v >> 24) > (row[px] >> 24)) row[px] = v;
            }
        }
    }

    // ---- Vector styles: shared pens/brushes ------------------------------------------------------

    private Pen[]? _pens;

    private Pen[] Pens()
    {
        if (_pens != null) return _pens;
        Color c0 = BlockColor, c1 = PeakColor;
        Pen Make(Color c, byte a, double w)
        {
            var p = new Pen(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)), w) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            p.Freeze();
            return p;
        }
        _pens = new[] { Make(c0, 0x30, 12), Make(c0, 0x70, 5), Make(c1, 0xF0, 1.8), Make(c1, 0xC0, 1.4) };
        return _pens;
    }

    // ---- 7. Oscilloscope ------------------------------------------------------------------------

    private readonly float[] _wave = new float[2048];
    private float[] _wavePts = Array.Empty<float>();
    private float _waveGain = 2f, _waveEnergy;

    private void UpdateWave(bool active)
    {
        int pts = Math.Max(32, (int)(ActualWidth / 3));
        if (_wavePts.Length != pts) _wavePts = new float[pts];

        if (active && Analyzer != null)
        {
            Analyzer.GetWaveform(_wave);
            // Trigger on a rising zero crossing so the wave stands still instead of scrolling.
            int window = 1024, trig = 0;
            for (int i = 1; i < _wave.Length - window; i++)
                if (_wave[i - 1] < 0 && _wave[i] >= 0) { trig = i; break; }
            float peak = 0.02f;
            for (int i = 0; i < window; i++) peak = Math.Max(peak, Math.Abs(_wave[trig + i]));
            _waveGain += (Math.Min(8f, 0.85f / peak) - _waveGain) * 0.08f;
            float energy = 0;
            for (int p = 0; p < pts; p++)
            {
                int si = trig + p * window / pts;
                float avg = 0; int cnt = 0;
                for (int j = Math.Max(0, si - 6); j <= Math.Min(_wave.Length - 1, si + 6); j++) { avg += _wave[j]; cnt++; }
                float s = avg / cnt * _waveGain;
                _wavePts[p] += (Math.Clamp(s, -1.2f, 1.2f) - _wavePts[p]) * 0.45f;
                energy = Math.Max(energy, Math.Abs(_wavePts[p]));
            }
            _waveEnergy = energy;
        }
        else
        {
            float energy = 0;
            for (int p = 0; p < pts; p++) { _wavePts[p] *= 0.85f; energy = Math.Max(energy, Math.Abs(_wavePts[p])); }
            _waveEnergy = energy;
        }
    }

    private void RenderOscilloscope(DrawingContext dc)
    {
        if (_wavePts.Length < 2) return;
        double w = ActualWidth, h = ActualHeight, cy = h / 2, amp = h * 0.36;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, cy - _wavePts[0] * amp), false, false);
            var list = new List<Point>(_wavePts.Length);
            for (int i = 1; i < _wavePts.Length; i++)
            {
                double x = w * i / (_wavePts.Length - 1);
                // Taper toward the edges so the line fades into the window.
                double taper = Math.Sin(Math.PI * i / (_wavePts.Length - 1));
                list.Add(new Point(x, cy - _wavePts[i] * amp * (0.35 + 0.65 * taper)));
            }
            ctx.PolyLineTo(list, true, true);
        }
        g.Freeze();
        var pens = Pens();
        dc.DrawGeometry(null, pens[0], g);
        dc.DrawGeometry(null, pens[1], g);
        dc.DrawGeometry(null, pens[2], g);
    }

    // ---- 8. Mirror mountains --------------------------------------------------------------------

    private void RenderMountains(DrawingContext dc)
    {
        int n = Math.Min(40, _level.Length);
        if (n < 4) return;
        double w = ActualWidth, h = ActualHeight, cx = w / 2, cy = h / 2;
        double artHalf = ArtSize / 2, margin = 26;
        double inner = artHalf + margin, outer = Math.Max(inner + 40, cx - 14);
        double maxH = Math.Min(cy - 8, ArtSize * 0.62);
        Color c0 = BlockColor, c1 = PeakColor;

        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xE0, c1.R, c1.G, c1.B), 0),
                new GradientStop(Color.FromArgb(0x90, c0.R, c0.G, c0.B), 0.55),
                new GradientStop(Color.FromArgb(0x30, c0.R, c0.G, c0.B), 1),
            },
        };
        fill.Freeze();
        var pens = Pens();

        foreach (int dir in new[] { 1, -1 })
        {
            var pts = new Point[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / (n - 1);
                double fade = 1 - 0.4 * t;
                pts[i] = new Point(cx + dir * (inner + (outer - inner) * t), cy - _level[i] * maxH * fade - 1.5);
            }
            var upper = SmoothArea(pts, cy);
            dc.DrawGeometry(fill, null, upper);
            dc.DrawGeometry(null, pens[3], SmoothLine(pts));
            // Reflection below the center line.
            dc.PushTransform(new ScaleTransform(1, -1, 0, cy));
            dc.PushOpacity(0.32);
            dc.DrawGeometry(fill, null, upper);
            dc.Pop();
            dc.Pop();
        }
    }

    private static StreamGeometry SmoothArea(Point[] pts, double baseY)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, baseY), true, true);
            ctx.LineTo(pts[0], false, true);
            AddCatmullRom(ctx, pts);
            ctx.LineTo(new Point(pts[^1].X, baseY), false, true);
        }
        g.Freeze();
        return g;
    }

    private static StreamGeometry SmoothLine(Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            AddCatmullRom(ctx, pts);
        }
        g.Freeze();
        return g;
    }

    private static void AddCatmullRom(StreamGeometryContext ctx, Point[] p)
    {
        for (int i = 0; i < p.Length - 1; i++)
        {
            var p0 = p[Math.Max(0, i - 1)];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = p[Math.Min(p.Length - 1, i + 2)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            ctx.BezierTo(c1, c2, p2, true, true);
        }
    }

    // ---- 9. Bass halo ---------------------------------------------------------------------------

    private void RenderHalo(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var center = new Point(w / 2, h / 2);
        double artHalf = ArtSize / 2;
        Color c0 = BlockColor, c1 = PeakColor;

        // Outer bass glow.
        double r1 = artHalf * (1.25 + _bass * 0.85);
        DrawGlow(dc, center, r1, c0, 0.18 + 0.6 * _bass, 0.55, w, h);
        // Tighter mid-range shimmer.
        double r2 = artHalf * (1.12 + _mid * 0.35);
        DrawGlow(dc, center, r2, c1, 0.08 + 0.4 * _mid, 0.72, w, h);
    }

    private static void DrawGlow(DrawingContext dc, Point center, double radius, Color c, double alpha, double solidTo, double w, double h)
    {
        byte a = (byte)(Math.Clamp(alpha, 0, 1) * 255);
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = center, GradientOrigin = center,
            RadiusX = radius, RadiusY = radius,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(a, c.R, c.G, c.B), solidTo),
                new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
            },
        };
        brush.Freeze();
        dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
    }

    private double _artScale = 1;

    /// <summary>Eases the album art toward a scale (ring makes room; halo breathes with the bass).</summary>
    private void SetArtScale(double target, float dt, bool fast = false)
    {
        if (ArtElement is not UIElement art) return;
        if (art.RenderTransform is not ScaleTransform st || st.IsFrozen)
        {
            st = new ScaleTransform(1, 1);
            art.RenderTransformOrigin = new Point(0.5, 0.5);
            art.RenderTransform = st;
        }
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        double k = fast ? 1 : Math.Min(1, dt * 7);
        _artScale += (target - _artScale) * k;
        st.ScaleX = st.ScaleY = _artScale;
        if (Math.Abs(target - _artScale) > 0.001) _forceRedraw = true; // keep animating until settled
    }

    private void ResetArtScale()
    {
        _artScale = 1;
        if (ArtElement?.RenderTransform is ScaleTransform st && !st.IsFrozen)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        }
    }}
