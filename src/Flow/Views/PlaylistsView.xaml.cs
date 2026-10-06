using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Flow.Library;
using Flow.ViewModels;
using Microsoft.Win32;

namespace Flow.Views;

public partial class PlaylistsView : UserControl
{
    private bool _dragEnabled;

    public PlaylistsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (Pl == null || _dragEnabled) return;
            _dragEnabled = true;
            DragReorder.Enable(TrackList, () => Pl.IsUserPlaylist, (from, to) => Pl.Move(from, to));
        };
    }

    private MainViewModel? Main => DataContext as MainViewModel;
    private PlaylistsViewModel? Pl => Main?.Playlists;

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = FlowDialog.Prompt("New playlist", "Give your playlist a name", "New Playlist");
        if (!string.IsNullOrWhiteSpace(name)) Pl?.CreatePlaylist(name.Trim());
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Pl?.Selected is not { IsSmart: false } sel) return;
        var name = FlowDialog.Prompt("Rename playlist", "Enter a new name", sel.Name);
        if (!string.IsNullOrWhiteSpace(name)) Pl.Rename(sel, name);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Pl?.Selected is not { IsSmart: false } sel) return;
        if (FlowDialog.Confirm("Delete playlist?", $"“{sel.Name}” will be removed. Your music files are not affected.", "Delete"))
            Pl.Delete(sel);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Import playlist", Filter = "Playlists|*.m3u;*.m3u8|All files|*.*" };
        if (dlg.ShowDialog() == true) Pl?.Import(dlg.FileName);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Pl?.Selected == null || Pl.Tracks.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export playlist",
            Filter = "M3U8 playlist|*.m3u8",
            FileName = string.Concat(Pl.Selected.Name.Split(System.IO.Path.GetInvalidFileNameChars())) + ".m3u8",
        };
        if (dlg.ShowDialog() == true) Pl.Export(dlg.FileName);
    }

    private void TrackList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var c = DragReorder.FindContainer(e.OriginalSource as DependencyObject);
        if (c?.DataContext is Track t) Pl?.PlayFrom(t);
    }

    private void TrackList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Pl?.IsUserPlaylist == true)
        {
            Pl.RemoveFromSelected(TrackList.SelectedItems.OfType<Track>().ToList());
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && TrackList.SelectedItem is Track t)
        {
            Pl?.PlayFrom(t);
            e.Handled = true;
        }
    }

    private void TrackList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Main == null || Pl == null || TrackList.ContextMenu == null) return;
        var c = DragReorder.FindContainer(e.OriginalSource as DependencyObject);
        if (c == null) { e.Handled = true; return; }
        if (!c.IsSelected)
        {
            TrackList.SelectedItems.Clear();
            c.IsSelected = true;
        }
        var tracks = TrackList.SelectedItems.OfType<Track>().ToList();
        var extra = Pl.IsUserPlaylist
            ? new[] { TrackMenu.Item("Remove from this playlist", "", () => Pl.RemoveFromSelected(tracks)) }
            : Array.Empty<MenuItem>();
        TrackMenu.Populate(TrackList.ContextMenu, Main, tracks, extra);
    }
}
