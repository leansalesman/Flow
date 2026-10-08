using System.Windows;
using System.Windows.Controls;
using Flow.Library;
using Flow.ViewModels;

namespace Flow.Views;

/// <summary>
/// iTunes-style editing for local songs: rename, edit details, edit a whole album (including the track order)
/// and choose cover artwork. Changes are written into the audio files. Spotify songs are never offered these.
/// </summary>
public static class LocalEdits
{
    /// <summary>True when every song is a local file (so the edit actions apply).</summary>
    public static bool AllLocal(IReadOnlyList<Track> tracks) => tracks.Count > 0 && tracks.All(t => !t.IsSpotify);

    /// <summary>The menu items for local songs; none for Spotify songs.</summary>
    public static IEnumerable<object> MenuItems(MainViewModel vm, IReadOnlyList<Track> tracks)
    {
        if (!AllLocal(tracks)) yield break;
        var list = tracks.ToList();
        yield return new Separator();
        if (list.Count == 1)
        {
            yield return TrackMenu.Item("Rename…", "", () => Rename(vm, list[0]));
            yield return TrackMenu.Item("Edit info…", "", () => EditInfo(vm, list[0]));
        }
        var albums = AlbumsOf(vm, list);
        if (albums.Count == 1)
            yield return TrackMenu.Item("Edit album…", "", () => EditAlbum(vm, albums[0]));
        yield return TrackMenu.Item("Choose cover artwork…", "", () => ChooseCover(vm, albums.SelectMany(a => a).ToList()));
    }

    /// <summary>The local songs of each album the given songs belong to.</summary>
    private static List<List<Track>> AlbumsOf(MainViewModel vm, List<Track> tracks)
    {
        var keys = tracks.Select(t => t.ArtKey).ToHashSet();
        return vm.LibraryService.Snapshot()
            .Where(t => !t.IsSpotify && keys.Contains(t.ArtKey))
            .GroupBy(t => t.ArtKey)
            .Select(g => g.ToList())
            .ToList();
    }

    // ---- Rename ----

    public static void Rename(MainViewModel vm, Track t)
    {
        if (t.IsSpotify) return;
        var name = FlowDialog.Prompt("Rename song", "The new name is saved in the song's file.", t.Title);
        if (name == null || string.IsNullOrWhiteSpace(name) || name.Trim() == t.Title) return;
        _ = SaveAsync(vm, new[] { new TrackEdit(t.Path) { Title = name.Trim() } }, "Renamed “" + name.Trim() + "”");
    }

    // ---- Edit info (one song) ----

    public static void EditInfo(MainViewModel vm, Track t)
    {
        if (t.IsSpotify) return;
        var grid = Form(out var add);
        var title = add("Title", t.Title);
        var artist = add("Artist", t.Artist);
        var album = add("Album", t.Album);
        var albumArtist = add("Album artist", t.AlbumArtist);
        var genre = add("Genre", t.Genre);
        var year = add("Year", t.Year > 0 ? t.Year.ToString() : "");
        var track = add("Track number", t.TrackNumber > 0 ? t.TrackNumber.ToString() : "");
        var disc = add("Disc number", t.DiscNumber > 0 ? t.DiscNumber.ToString() : "");
        var error = ErrorText();
        var body = new StackPanel { Width = 440 };
        body.Children.Add(grid);
        body.Children.Add(error);
        var file = new TextBlock { Text = t.Path, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 12, 0, 0) };
        file.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        body.Children.Add(file);

        TrackEdit? edit = null;
        bool saved = FlowDialog.ShowForm("Edit info", "", body, "Save", () =>
        {
            if (!Number(year.Text, "Year", error, out var y) || !Number(track.Text, "Track number", error, out var n) ||
                !Number(disc.Text, "Disc number", error, out var d)) return false;
            edit = new TrackEdit(t.Path);
            if (title.Text.Trim() != t.Title) edit.Title = title.Text;
            if (artist.Text.Trim() != t.Artist) edit.Artist = artist.Text;
            if (album.Text.Trim() != t.Album) edit.Album = album.Text;
            if (albumArtist.Text.Trim() != t.AlbumArtist) edit.AlbumArtist = albumArtist.Text;
            if (genre.Text.Trim() != t.Genre) edit.Genre = genre.Text;
            if (y != t.Year) edit.Year = y;
            if (n != t.TrackNumber) edit.TrackNumber = n;
            if (d != t.DiscNumber) edit.DiscNumber = d;
            return true;
        });
        if (saved && edit != null) _ = SaveAsync(vm, new[] { edit }, "Saved “" + (edit.Title?.Trim() ?? t.Title) + "”");
    }

    // ---- Edit album (details for every song + track order) ----

    public static void EditAlbum(MainViewModel vm, IReadOnlyList<Track> albumTracks)
    {
        var tracks = albumTracks.Where(t => !t.IsSpotify)
            .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber == 0 ? int.MaxValue : t.TrackNumber)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (tracks.Count == 0) return;
        var first = tracks[0];
        string Common(Func<Track, string> f) => tracks.Select(f).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
        string origAlbum = Common(t => t.Album), origAlbumArtist = Common(t => t.AlbumArtist), origGenre = Common(t => t.Genre);
        int origYear = tracks.Select(t => t.Year).Where(y => y > 0).DefaultIfEmpty(0).Max();

        var grid = Form(out var add);
        var album = add("Album", origAlbum);
        var albumArtist = add("Album artist", origAlbumArtist);
        var genre = add("Genre", origGenre);
        var year = add("Year", origYear > 0 ? origYear.ToString() : "");

        var heading = new TextBlock { Text = "Songs", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 2) };
        var hint = new TextBlock { Text = "Use the arrows to change the order. Track numbers follow the order shown.", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");

        var rows = tracks.Select(t => new AlbumRow(t)).ToList();
        var rowPanel = new StackPanel();
        void Layout()
        {
            rowPanel.Children.Clear();
            for (int i = 0; i < rows.Count; i++) rowPanel.Children.Add(rows[i].Build(i, rows.Count, Move));
        }
        void Move(int from, int to)
        {
            if (to < 0 || to >= rows.Count) return;
            var r = rows[from];
            rows.RemoveAt(from);
            rows.Insert(to, r);
            Layout();
        }
        Layout();

        var columns = new Grid { Margin = new Thickness(0, 10, 0, 4) };
        AlbumRow.Columns(columns);
        void Col(int c, string text)
        {
            var tb = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            Grid.SetColumn(tb, c);
            columns.Children.Add(tb);
        }
        Col(0, "#"); Col(1, "Title"); Col(2, "Artist"); Col(3, "Disc");

        var scroller = new ScrollViewer { Content = rowPanel, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var error = ErrorText();
        var body = new StackPanel { Width = 680 };
        body.Children.Add(grid);
        body.Children.Add(heading);
        body.Children.Add(hint);
        body.Children.Add(columns);
        body.Children.Add(scroller);
        body.Children.Add(error);

        List<TrackEdit>? edits = null;
        bool saved = FlowDialog.ShowForm("Edit album", "", body, "Save", () =>
        {
            if (!Number(year.Text, "Year", error, out var y)) return false;
            foreach (var r in rows)
                if (!Number(r.Disc.Text, "Disc", error, out _)) return false;
            edits = new List<TrackEdit>();
            var counters = new Dictionary<int, int>();
            var perDisc = rows.GroupBy(r => int.TryParse(r.Disc.Text, out var dd) ? dd : 0).ToDictionary(g => g.Key, g => g.Count());
            foreach (var r in rows)
            {
                var t = r.Track;
                int disc = int.TryParse(r.Disc.Text, out var dv) ? dv : 0;
                int number = counters[disc] = (counters.TryGetValue(disc, out var c) ? c : 0) + 1;
                var e = new TrackEdit(t.Path);
                bool changed = false;
                if (album.Text.Trim() != origAlbum && album.Text.Trim() != t.Album) { e.Album = album.Text; changed = true; }
                if (albumArtist.Text.Trim() != origAlbumArtist && albumArtist.Text.Trim() != t.AlbumArtist) { e.AlbumArtist = albumArtist.Text; changed = true; }
                if (genre.Text.Trim() != origGenre && genre.Text.Trim() != t.Genre) { e.Genre = genre.Text; changed = true; }
                if (y != origYear && y != t.Year) { e.Year = y; changed = true; }
                if (r.Title.Text.Trim() != t.Title) { e.Title = r.Title.Text; changed = true; }
                if (r.Artist.Text.Trim() != t.Artist) { e.Artist = r.Artist.Text; changed = true; }
                if (disc != t.DiscNumber) { e.DiscNumber = disc; changed = true; }
                if (number != t.TrackNumber) { e.TrackNumber = number; e.TrackCount = perDisc[disc]; changed = true; }
                if (changed) edits.Add(e);
            }
            return true;
        });
        if (saved && edits is { Count: > 0 })
            _ = SaveAsync(vm, edits, "Saved “" + (string.IsNullOrWhiteSpace(album.Text) ? first.DisplayAlbum : album.Text.Trim()) + "”",
                          followAlbumOf: first.Path);
    }

    /// <summary>One editable song row in Edit album.</summary>
    private sealed class AlbumRow
    {
        public AlbumRow(Track t)
        {
            Track = t;
            Title = Box(t.Title);
            Artist = Box(t.Artist);
            Disc = Box(t.DiscNumber > 0 ? t.DiscNumber.ToString() : "");
        }

        public Track Track { get; }
        public TextBox Title { get; }
        public TextBox Artist { get; }
        public TextBox Disc { get; }

        public static void Columns(Grid g)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        }

        public UIElement Build(int index, int count, Action<int, int> move)
        {
            foreach (var b in new[] { Title, Artist, Disc })
                if (b.Parent is Panel p) p.Children.Remove(b);
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            Columns(g);
            var num = new TextBlock { Text = (index + 1).ToString(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            num.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            g.Children.Add(num);
            Place(g, Title, 1); Place(g, Artist, 2); Place(g, Disc, 3);
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            arrows.Children.Add(Arrow("", "Move up", index > 0, () => move(index, index - 1)));
            arrows.Children.Add(Arrow("", "Move down", index < count - 1, () => move(index, index + 1)));
            Grid.SetColumn(arrows, 4);
            g.Children.Add(arrows);
            return g;
        }

        private static void Place(Grid g, UIElement e, int col)
        {
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        private static Button Arrow(string glyph, string tip, bool enabled, Action click)
        {
            var b = new Button { Content = glyph, ToolTip = tip, IsEnabled = enabled, Width = 28, Height = 28, FontSize = 11 };
            b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            b.Click += (_, _) => click();
            return b;
        }

        private static TextBox Box(string text)
        {
            return Compact(new TextBox { Text = text, Margin = new Thickness(0, 0, 6, 0) });
        }
    }

    // ---- Cover artwork ----

    public static void ChooseCover(MainViewModel vm, IReadOnlyList<Track> tracks)
    {
        var local = tracks.Where(t => !t.IsSpotify).ToList();
        if (local.Count == 0) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose cover artwork",
            Filter = TagWriter.ImageFilter,
            Multiselect = false,
        };
        try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(local[0].Path); } catch { }
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;

        byte[] jpeg;
        try { jpeg = TagWriter.PrepareCover(dlg.FileName); }
        catch (Exception ex)
        {
            vm.ShowToast(ex.Message);
            return;
        }
        _ = CoverAsync(vm, local, jpeg);
    }

    private static async Task CoverAsync(MainViewModel vm, List<Track> tracks, byte[] jpeg)
    {
        var keys = tracks.Select(t => t.ArtKey).ToHashSet();
        var failed = await vm.LibraryService.SetCoverAsync(tracks, jpeg);
        vm.Library.RefreshAfterEdit(null);
        if (vm.Playback.CurrentTrack is { } cur && keys.Contains(cur.ArtKey)) vm.Playback.ReloadArt();
        Report(vm, failed, tracks.Count, "Cover artwork updated");
    }

    // ---- Shared ----

    private static async Task SaveAsync(MainViewModel vm, IReadOnlyList<TrackEdit> edits, string doneText, string? followAlbumOf = null)
    {
        var failed = await vm.LibraryService.EditAsync(edits);
        string? key = followAlbumOf != null ? vm.LibraryService.Find(followAlbumOf)?.ArtKey : null;
        vm.Library.RefreshAfterEdit(key);
        vm.Playback.ReloadArt();
        Report(vm, failed, edits.Count, doneText);
    }

    private static void Report(MainViewModel vm, List<string> failed, int total, string doneText)
    {
        if (failed.Count == 0) { vm.ShowToast(doneText); return; }
        var first = failed[0];
        vm.ShowToast(failed.Count == total && total == 1
            ? "Couldn't save. " + first[(first.IndexOf(": ", StringComparison.Ordinal) + 2)..]
            : $"{failed.Count} of {total} songs weren't changed. {first}");
    }

    private static Grid Form(out Func<string, string, TextBox> add)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        add = (label, value) =>
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            l.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetRow(l, row);
            grid.Children.Add(l);
            var box = Compact(new TextBox { Text = value, Margin = new Thickness(0, 3, 0, 3) });
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            return box;
        };
        return grid;
    }

    /// <summary>The themed text box, sized for forms.</summary>
    private static TextBox Compact(TextBox b)
    {
        b.SetResourceReference(FrameworkElement.StyleProperty, "PlainTextBox");
        b.Padding = new Thickness(8, 2, 8, 2);
        b.FontSize = 13;
        b.VerticalContentAlignment = VerticalAlignment.Center;
        return b;
    }

    private static TextBlock ErrorText()
    {
        var t = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        t.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x5A, 0x5A));
        return t;
    }

    /// <summary>Empty = 0; otherwise a whole number from 0 to 9999.</summary>
    private static bool Number(string text, string field, TextBlock error, out int value)
    {
        value = 0;
        text = text.Trim();
        if (text.Length == 0 || (int.TryParse(text, out value) && value >= 0 && value <= 9999))
        {
            error.Visibility = Visibility.Collapsed;
            return true;
        }
        error.Text = $"{field} must be a number.";
        error.Visibility = Visibility.Visible;
        return false;
    }
}
