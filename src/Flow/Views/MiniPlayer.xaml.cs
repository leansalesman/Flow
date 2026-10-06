using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Flow.ViewModels;

namespace Flow.Views;

/// <summary>The player bar shown on Library / Playlists / Settings (Now Playing has its own big controls).</summary>
public partial class MiniPlayer : UserControl
{
    public MiniPlayer() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Seeking works like the Now Playing timeline: position updates pause while the user drags.
    private void Timeline_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm != null) Vm.Playback.IsUserSeeking = true;
    }

    private void Timeline_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        Vm.Playback.Seek(Timeline.Value);
        Vm.Playback.IsUserSeeking = false;
    }

    private void Timeline_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (Vm != null) Vm.Playback.IsUserSeeking = true;
    }

    private void Timeline_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (Vm == null) return;
        Vm.Playback.Seek(Timeline.Value);
        Vm.Playback.IsUserSeeking = false;
    }
}
