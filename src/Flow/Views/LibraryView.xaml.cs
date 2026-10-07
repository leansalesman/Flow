using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Flow.Library;
using Flow.ViewModels;

using Track = Flow.Library.Track;

namespace Flow.Views;

public partial class LibraryView : UserControl
{
    private const double TileGap = 22;

    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (Lib != null)
            {
                Lib.PropertyChanged -= OnLibChanged;
                Lib.PropertyChanged += OnLibChanged;
                SyncColumnSortGlyphs();
            }
        };
    }

    private MainViewModel? Main => DataContext as MainViewModel;
    private LibraryViewModel? Lib => Main?.Library;

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnLibChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.ArtSize)) LayoutShelves();
        if (e.PropertyName is nameof(LibraryViewModel.SortField) or nameof(LibraryViewModel.SortDescending) or nameof(LibraryViewModel.Tracks))
            Dispatcher.BeginInvoke(SyncColumnSortGlyphs, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---- Shelves sizing: fill the width with evenly sized tiles ----

    private void Shelves_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutShelves();

    /// <summary>Fits as many tiles of the chosen artwork size as the width allows, stretched to fill the row.</summary>
    private void LayoutShelves()
    {
        if (Lib == null) return;
        double MinTile = Lib.MinTileWidth;
        double width = Shelves.ActualWidth - 18; // scrollbar allowance
        if (width <= 0) return;
        int cols = Math.Max(1, (int)((width + TileGap) / (MinTile + TileGap)));
        double tile = Math.Floor((width - TileGap * cols) / cols);
        Lib.TileWidth = Math.Max(120, tile);
        Lib.ColumnsPerRow = cols;
        // Decode covers at the size they're drawn (tile width × display scale), not larger.
        if (Main != null)
            Main.LibraryService.Art.ThumbDecodePx = (int)Math.Ceiling(Lib.TileWidth * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    /// <summary>The mouse wheel scrolls the genre row sideways.</summary>
    private void GenreScroller_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        GenreScroller.ScrollToHorizontalOffset(GenreScroller.HorizontalOffset - e.Delta * 0.6);
        e.Handled = true;
    }

    private void TilePlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AlbumInfo a) Lib?.PlayAlbumCommand.Execute(a);
        e.Handled = true;
    }

    private void Album_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button b || b.DataContext is not AlbumInfo a || b.ContextMenu == null || Main == null) return;
        TrackMenu.Populate(b.ContextMenu, Main, a.Tracks,
            TrackMenu.Item("Open album", "î£±", () => Lib!.SelectedAlbum = a));
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e) => Main?.Settings.AddFolder();

    // ---- Sort menu ----

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (Lib == null) return;
        var menu = new ContextMenu { PlacementTarget = SortButton, Placement = PlacementMode.Bottom };
        foreach (var (field, label) in LibraryViewModel.SortOptions)
        {
            var f = field;
            var mi = new MenuItem { Header = label, IsCheckable = false, IsChecked = Lib.SortField == f };
            mi.Click += (_, _) => Lib.SetSort(f);
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var asc = new MenuItem { Header = "Ascending", IsChecked = !Lib.SortDescending };
        asc.Click += (_, _) => Lib.SortDescending = false;
        var desc = new MenuItem { Header = "Descending", IsChecked = Lib.SortDescending };
        desc.Click += (_, _) => Lib.SortDescending = true;
        menu.Items.Add(asc);
        menu.Items.Add(desc);
        menu.IsOpen = true;
    }

    // ---- Track table ----

    private void Table_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // sorting is done by the view model (also drives the album shelves)
        if (Lib == null) return;
        if (Enum.TryParse<SortField>(e.Column.SortMemberPath, out var f)) Lib.SetSort(f);
    }

    private void SyncColumnSortGlyphs()
    {
        if (Lib == null) return;
        foreach (var col in Table.Columns)
        {
            col.SortDirection = col.SortMemberPath == Lib.SortField.ToString()
                ? (Lib.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    private void Table_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DragReorder.FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return; // clicked a link
        var row = DragReorder.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is Track t) Lib?.PlayFromTable(t);
    }

    private void Table_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Table.SelectedItem is Track t)
        {
            Lib?.PlayFromTable(t);
            e.Handled = true;
        }
    }

    private void Table_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Main == null || Table.ContextMenu == null) return;
        var row = DragReorder.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row == null) { e.Handled = true; return; }
        if (!row.IsSelected)
        {
            Table.SelectedItems.Clear();
            row.IsSelected = true;
        }
        var tracks = Table.SelectedItems.OfType<Track>().ToList();
        TrackMenu.Populate(Table.ContextMenu, Main, tracks);
    }

    // ---- Album detail ----

    /// <summary>
    /// Softly blurs and dims the shelves behind the album panel so its text stays readable.
    /// The blur effect is removed again when closed (effects cost rendering time even at radius 0).
    /// </summary>
    private void SetBackgroundSoftened(bool soften)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(soften ? 260 : 200);

        if (LibraryContent.Effect is not System.Windows.Media.Effects.BlurEffect blur)
        {
            blur = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 0,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            };
            LibraryContent.Effect = blur;
        }

        var radius = new DoubleAnimation(soften ? 22 : 0, duration) { EasingFunction = ease };
        if (!soften)
            radius.Completed += (_, _) =>
            {
                if (!AlbumOverlay.IsVisible) LibraryContent.Effect = null;
            };
        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, radius);
        LibraryContent.BeginAnimation(OpacityProperty, new DoubleAnimation(soften ? 0.35 : 1, duration) { EasingFunction = ease });
        LibraryContent.IsHitTestVisible = !soften;
    }

    private bool _softened;

    /// <summary>Album and artist panels share the blurred background; the panel that appears slides in.</summary>
    private void Overlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement panel && panel.IsVisible)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            var tt = new System.Windows.Media.TranslateTransform(24, 0);
            panel.RenderTransform = tt;
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }
        // Evaluate after both panels have updated (artist → album swaps one for the other).
        Dispatcher.BeginInvoke(() =>
        {
            bool open = AlbumOverlay.IsVisible || ArtistOverlay.IsVisible;
            if (open == _softened) return;
            _softened = open;
            SetBackgroundSoftened(open);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ArtistTracks_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DragReorder.FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return; // clicked a link
        var c = DragReorder.FindContainer(e.OriginalSource as DependencyObject);
        if (c?.DataContext is Track t) Lib?.PlayFromArtist(t);
    }

    private void ArtistTracks_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Main == null || ArtistTracks.ContextMenu == null) return;
        var c = DragReorder.FindContainer(e.OriginalSource as DependencyObject);
        if (c == null) { e.Handled = true; return; }
        if (!c.IsSelected)
        {
            ArtistTracks.SelectedItems.Clear();
            c.IsSelected = true;
        }
        TrackMenu.Populate(ArtistTracks.ContextMenu, Main, ArtistTracks.SelectedItems.OfType<Track>().ToList());
    }

    private void AlbumTracks_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var c = DragReorder.FindContainer(e.OriginalSource as DependencyObject);
        if (c?.DataContext is Track t) Lib?.PlayFromAlbum(t);
    }

    private void AlbumTracks_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Main == null || AlbumTracks.ContextMenu == null) return;
        var c = DragReorder.FindContainer(e.OriginalSource as DependencyObject);
        if (c == null) { e.Handled = true; return; }
        if (!c.IsSelected)
        {
            AlbumTracks.SelectedItems.Clear();
            c.IsSelected = true;
        }
        TrackMenu.Populate(AlbumTracks.ContextMenu, Main, AlbumTracks.SelectedItems.OfType<Track>().ToList());
    }
}
