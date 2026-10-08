using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Flow.Library;
using Flow.ViewModels;

namespace Flow.Views;

/// <summary>Builds the shared right-click menu for one or more tracks.</summary>
public static class TrackMenu
{
    public static void Populate(ContextMenu menu, MainViewModel vm, IReadOnlyList<Track> tracks, params MenuItem[] extra)
    {
        menu.Items.Clear();
        if (tracks.Count == 0) return;
        var list = tracks.ToList();

        menu.Items.Add(Item("Play", "", () => vm.Playback.PlayTracks(list)));
        menu.Items.Add(Item("Play next", "", () => vm.Playback.PlayNext(list)));
        menu.Items.Add(Item("Add to queue", "", () => vm.Playback.AddToQueue(list)));

        var pl = new MenuItem { Header = "Add to playlist", Icon = "" };
        pl.Items.Add(Item("New playlist…", "", () =>
        {
            var name = FlowDialog.Prompt("New playlist", "Give your playlist a name", DefaultName(list));
            if (!string.IsNullOrWhiteSpace(name))
            {
                vm.Playlists.CreatePlaylist(name.Trim(), list);
                vm.ShowToast($"Created “{name.Trim()}”");
            }
        }));
        var user = vm.Playlists.UserPlaylists.ToList();
        if (user.Count > 0) pl.Items.Add(new Separator());
        foreach (var p in user)
        {
            var id = p.Id;
            pl.Items.Add(Item(p.Name, null, () => vm.Playlists.AddTo(id, list)));
        }
        menu.Items.Add(pl);

        menu.Items.Add(new Separator());
        bool allFav = list.All(t => t.IsFavorite);
        menu.Items.Add(Item(allFav ? "Remove from Favorites" : "Add to Favorites", allFav ? "" : "", () =>
        {
            foreach (var t in list)
            {
                t.IsFavorite = !allFav;
                vm.LibraryService.SaveStats(t);
            }
            if (vm.Playlists.Selected?.Smart == SmartKind.Favorites) vm.Playlists.LoadTracks();
        }));

        if (list.Count == 1 && list[0].IsSpotify)
        {
            var uri = list[0].SpotifyUri!;
            menu.Items.Add(Item("Open in Spotify", "", () =>
            {
                try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
            }));
        }
        else if (list.Count == 1)
        {
            var path = list[0].Path;
            menu.Items.Add(Item("Show in File Explorer", "", () =>
            {
                try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
            }));
        }

        // Local files only: rename, edit info, edit album, choose cover artwork.
        foreach (var m in LocalEdits.MenuItems(vm, list)) menu.Items.Add(m);

        if (extra.Length > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var m in extra) menu.Items.Add(m);
        }
    }

    public static MenuItem Item(string header, string? glyph, Action action)
    {
        var mi = new MenuItem { Header = header };
        if (glyph != null) mi.Icon = glyph;
        mi.Click += (_, _) => action();
        return mi;
    }

    private static string DefaultName(List<Track> tracks)
    {
        var album = tracks.Select(t => t.Album).Distinct().ToList();
        return album.Count == 1 && !string.IsNullOrWhiteSpace(album[0]) ? album[0] : "New Playlist";
    }
}

/// <summary>
/// "Play on…": a menu of the Spotify Connect devices (this PC, phones, speakers…), opened above a button.
/// Picking one sends Spotify songs there (moving a playing one); "Automatic" goes back to Spotify's active device.
/// </summary>
public static class DeviceMenu
{
    public static async void Show(FrameworkElement anchor, MainViewModel? vm)
    {
        if (vm == null) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        menu.Items.Add(new MenuItem { Header = "Looking for Spotify devices…", IsEnabled = false });
        menu.IsOpen = true;

        IReadOnlyList<Flow.Spotify.SpotifyDevice> devices;
        try { devices = await vm.Playback.GetSpotifyDevicesAsync(); }
        catch { devices = Array.Empty<Flow.Spotify.SpotifyDevice>(); }
        if (!menu.IsOpen) return;

        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = "Play Spotify songs on", IsEnabled = false });
        var chosen = vm.Playback.ChosenSpotifyDeviceId;
        var auto = TrackMenu.Item("Automatic  (Spotify's active device)", "\uE895", () => _ = vm.Playback.PlaySpotifyOnAsync(null));
        Check(auto, chosen == null);
        menu.Items.Add(auto);
        menu.Items.Add(new Separator());
        if (devices.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No devices found — open Spotify on a device", IsEnabled = false });
        foreach (var d in devices)
        {
            var item = TrackMenu.Item(d.IsActive ? $"{d.Name}  ·  active" : d.Name, Glyph(d.Type), () => _ = vm.Playback.PlaySpotifyOnAsync(d));
            Check(item, chosen == d.Id);
            menu.Items.Add(item);
        }
    }

    // The menu shows a check mark in the icon column, so a checked item drops its icon.
    private static void Check(MenuItem item, bool on)
    {
        if (!on) return;
        item.IsChecked = true;
        item.Icon = null;
    }

    private static string Glyph(string type) => type switch
    {
        "Computer" => "\uE7F8",
        "Smartphone" or "Tablet" => "\uE8EA",
        "TV" or "CastVideo" => "\uE7F4",
        _ => "\uE7F5",
    };
}

/// <summary>Drag-to-reorder for ListBoxes.</summary>
public static class DragReorder
{
    private const string Format = "FlowReorderIndex";

    public static void Enable(ListBox list, Func<bool> canReorder, Action<int, int> move)
    {
        Point start = default;
        int dragIndex = -1;

        list.AllowDrop = true;
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(list);
            var c = FindContainer(e.OriginalSource as DependencyObject);
            dragIndex = c != null ? list.ItemContainerGenerator.IndexFromContainer(c) : -1;
        };
        list.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || dragIndex < 0 || !canReorder()) return;
            var p = e.GetPosition(list);
            if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            int idx = dragIndex;
            dragIndex = -1;
            DragDrop.DoDragDrop(list, new DataObject(Format, idx), DragDropEffects.Move);
        };
        list.PreviewMouseLeftButtonUp += (_, _) => dragIndex = -1;
        list.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(Format)) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        };
        list.Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(Format)) return;
            int from = (int)e.Data.GetData(Format);
            var c = FindContainer(e.OriginalSource as DependencyObject);
            int to = c != null ? list.ItemContainerGenerator.IndexFromContainer(c) : list.Items.Count - 1;
            if (from >= 0 && to >= 0 && from != to) move(from, to);
            e.Handled = true;
        };
    }

    public static ListBoxItem? FindContainer(DependencyObject? d)
    {
        while (d != null && d is not ListBoxItem)
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }

    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}

/// <summary>Small themed prompt / confirm dialogs.</summary>
public static class FlowDialog
{
#if DEBUG
    /// <summary>--test-tags: positions and snapshots forms off-screen instead of showing them.</summary>
    internal static Action<Window>? ProbeHook;
#endif

    public static string? Prompt(string title, string message, string initial)
    {
        string? result = null;
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 14, 0, 0), MinWidth = 320 };
        box.SetResourceReference(FrameworkElement.StyleProperty, "PlainTextBox");
        var w = Build(title, message, box, "Save", ok => { if (ok) result = box.Text; });
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return result;
    }

    public static bool Confirm(string title, string message, string okText)
    {
        bool result = false;
        var w = Build(title, message, null, okText, ok => result = ok);
        w.ShowDialog();
        return result;
    }

    /// <summary>
    /// A themed form: <paramref name="body"/> under the title. <paramref name="onSave"/> runs on Save and keeps the
    /// dialog open by returning false (e.g. after showing a validation message). Returns true when saved.
    /// </summary>
    public static bool ShowForm(string title, string message, UIElement body, string okText, Func<bool> onSave)
    {
        bool saved = false;
        var w = Build(title, message, body, okText, ok => { if (ok) saved = true; }, () => onSave());
#if DEBUG
        ProbeHook?.Invoke(w);
#endif
        w.ShowDialog();
        return saved;
    }

    private static Window Build(string title, string message, UIElement? input, string okText, Action<bool> done,
                                Func<bool>? canClose = null)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title,
        };
        if (Application.Current.MainWindow is { IsVisible: true } owner && !ReferenceEquals(owner, w)) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.SetResourceReference(Window.FontFamilyProperty, "UiFont");
        w.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20), MinWidth = 340 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
        var msg = new TextBlock { Text = message, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
                                  Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible };
        msg.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        stack.Children.Add(msg);
        if (input != null) stack.Children.Add(input);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 90 };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "AccentPillButton");
        cancel.Click += (_, _) => { done(false); w.Close(); };
        ok.Click += (_, _) =>
        {
            if (canClose != null && !canClose()) return;
            done(true);
            w.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(16),
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.4 },
        };
        border.SetResourceReference(Border.BackgroundProperty, "MenuBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "MenuStrokeBrush");
        border.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch { } };
        w.Content = border;
        return w;
    }
}
