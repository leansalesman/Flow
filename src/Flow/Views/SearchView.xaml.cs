using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Flow.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        // Ready to type as soon as the page opens.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { Box.Focus(); Keyboard.Focus(Box); Box.SelectAll(); });
        };
    }
}
