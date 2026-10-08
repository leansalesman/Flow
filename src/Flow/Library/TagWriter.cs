using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flow.Library;

/// <summary>
/// One local song's new details. Null leaves a field as it is; an empty string or 0 clears it.
/// </summary>
public sealed class TrackEdit
{
    public TrackEdit(string path) => Path = path;
    public string Path { get; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Genre { get; set; }
    public int? Year { get; set; }
    public int? TrackNumber { get; set; }
    public int? TrackCount { get; set; }
    public int? DiscNumber { get; set; }
}

/// <summary>
/// Writes song details and cover art into local audio files (ID3, MP4, Vorbis comments… via TagLib#), the way
/// iTunes does, so edits survive rescans and show up in other apps. Never used for Spotify songs.
/// </summary>
public static class TagWriter
{
    /// <summary>The picture types offered in "Choose cover artwork".</summary>
    public const string ImageFilter = "Images (*.jpg, *.jpeg, *.png, *.webp)|*.jpg;*.jpeg;*.png;*.webp";

    /// <summary>Writes the edit; returns null, or why the file couldn't be changed.</summary>
    public static string? Apply(TrackEdit e)
    {
        return Write(e.Path, tag =>
        {
            if (e.Title != null) tag.Title = Clean(e.Title);
            if (e.Artist != null) tag.Performers = Names(e.Artist);
            if (e.Album != null) tag.Album = Clean(e.Album);
            if (e.AlbumArtist != null) tag.AlbumArtists = Names(e.AlbumArtist);
            if (e.Genre != null) tag.Genres = Names(e.Genre);
            if (e.Year != null) tag.Year = (uint)Math.Max(0, e.Year.Value);
            if (e.TrackNumber != null) tag.Track = (uint)Math.Max(0, e.TrackNumber.Value);
            if (e.TrackCount != null) tag.TrackCount = (uint)Math.Max(0, e.TrackCount.Value);
            if (e.DiscNumber != null) tag.Disc = (uint)Math.Max(0, e.DiscNumber.Value);
        });
    }

    /// <summary>Embeds a JPEG as the front cover (replacing any pictures); returns null, or why it failed.</summary>
    public static string? SetCover(string path, byte[] jpeg)
    {
        return Write(path, tag =>
        {
            var pic = new TagLib.Picture(new TagLib.ByteVector(jpeg))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg",
                Description = "Cover",
            };
            tag.Pictures = new TagLib.IPicture[] { pic };
        });
    }

    /// <summary>
    /// Reads a JPEG, PNG or WebP picture and returns it as a JPEG (at most 1200 px), the format every player
    /// understands inside audio files. Throws with a readable message when the picture can't be used.
    /// </summary>
    public static byte[] PrepareCover(string imagePath)
    {
        BitmapSource src;
        try
        {
            using var fs = File.OpenRead(imagePath);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            src = decoder.Frames[0];
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                Path.GetExtension(imagePath).Equals(".webp", StringComparison.OrdinalIgnoreCase)
                    ? "Windows couldn't read this WebP picture. Install \"WebP Image Extensions\" from the Microsoft Store, or use a JPG or PNG."
                    : "This picture couldn't be read.", ex);
        }
        if (src.PixelWidth < 16 || src.PixelHeight < 16) throw new InvalidOperationException("This picture is too small to use as cover art.");

        if (src.Format != PixelFormats.Bgr32 && src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32)
            src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        // JPEG has no transparency: put transparent pictures (PNG, WebP) on white.
        if (src.Format != PixelFormats.Bgr32)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, src.PixelWidth, src.PixelHeight));
                dc.DrawImage(src, new System.Windows.Rect(0, 0, src.PixelWidth, src.PixelHeight));
            }
            var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            src = rtb;
        }
        double scale = Math.Min(1.0, 1200.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        if (scale < 1.0) src = new TransformedBitmap(src, new ScaleTransform(scale, scale));

        var enc = new JpegBitmapEncoder { QualityLevel = 92 };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static string? Write(string path, Action<TagLib.Tag> change)
    {
        if (path.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) return "Spotify songs can't be edited.";
        try
        {
            if (!File.Exists(path)) return "The file is missing.";
            if (new FileInfo(path).IsReadOnly) return "The file is read-only.";
            using var f = TagLib.File.Create(path);
            change(f.Tag);
            f.Save();
            return null;
        }
        catch (IOException) { return "The file is in use (it may be playing). Try again when it's stopped."; }
        catch (UnauthorizedAccessException) { return "Flow isn't allowed to change this file."; }
        catch (TagLib.UnsupportedFormatException) { return "This file type can't store song details."; }
        catch (Exception ex) { return ex.Message; }
    }

    private static string Clean(string s) => s.Trim();

    private static string[] Names(string s)
    {
        s = s.Trim();
        return s.Length == 0 ? Array.Empty<string>() : new[] { s };
    }
}
