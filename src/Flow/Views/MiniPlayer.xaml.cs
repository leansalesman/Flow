using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Flow.ViewModels;

namespace Flow.Views;

/// <summary>The player bar shown on Library / Playlists / Settings (Now Playing has its own big controls).</summary>
public partial class MiniPlayer : UserControl
{
    public MiniPlayer()
    {
        InitializeComponent();
        TimelineSeek.Attach(Timeline);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Devices_Click(object sender, System.Windows.RoutedEventArgs e) => DeviceMenu.Show((System.Windows.FrameworkElement)sender, Vm);

}
