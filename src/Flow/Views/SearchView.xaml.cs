using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Flow.Spotify;
using Flow.ViewModels;

namespace Flow.Views;

/// <summary>
/// Search: the bar sits big in the middle of the page until there are results, then slides to the top (and back
/// to the middle when the search is cleared). The suggestion dropdown follows the arrow keys, Enter and Esc.
/// </summary>
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
        DataContextChanged += (_, _) =>
        {
            if (Vm != null)
            {
                Vm.PropertyChanged -= OnVmChanged;
                Vm.PropertyChanged += OnVmChanged;
                PlaceHero(animate: false);
            }
        };
    }

    private SearchViewModel? Vm => (DataContext as MainViewModel)?.Search;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchViewModel.IsResultsMode) or nameof(SearchViewModel.ShowSuggestions))
            Dispatcher.BeginInvoke(() => PlaceHero(animate: true), DispatcherPriority.Loaded);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceHero(animate: false);

    /// <summary>Centered on the page (nothing searched yet), or at the top above the results.</summary>
    private void PlaceHero(bool animate)
    {
        double target = 0;
        if (Vm is { IsResultsMode: false })
        {
            Hero.Measure(new Size(Hero.Width, double.PositiveInfinity));
            // Center the headline and bar; the dropdown hangs below without moving them.
            double heroH = Headline.DesiredSize.Height + Box.Height + 22;
            target = Math.Max(0, (Root.ActualHeight - heroH) / 2 - 40);
            // While suggestions show, ease up so the list has room under the bar.
            if (Vm.ShowSuggestions) target = Math.Min(target, Root.ActualHeight * 0.08);
        }
        // The list scrolls rather than running past the bottom of the page.
        double above = target + Hero.Margin.Top + (Vm is { IsResultsMode: false } ? Headline.DesiredSize.Height + 22 : 0) + Box.Height + 8;
        SuggestionList.MaxHeight = Math.Max(120, Root.ActualHeight - above - 24);
        if (!animate)
        {
            HeroShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            HeroShift.Y = target;
            return;
        }
        HeroShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---- Keyboard: arrows move through suggestions, Enter picks or searches, Esc closes or clears ----

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;
        switch (e.Key)
        {
            case Key.Down when vm.ShowSuggestions:
                vm.SelectedSuggestion = Math.Min(vm.Suggestions.Count - 1, vm.SelectedSuggestion + 1);
                e.Handled = true;
                break;
            case Key.Up when vm.ShowSuggestions:
                vm.SelectedSuggestion = Math.Max(-1, vm.SelectedSuggestion - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (vm.ShowSuggestions && vm.SelectedSuggestion >= 0 && vm.SelectedSuggestion < vm.Suggestions.Count)
                    _ = vm.PickAsync(vm.Suggestions[vm.SelectedSuggestion]);
                else
                    _ = vm.SubmitAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                if (vm.ShowSuggestions) vm.ShowSuggestions = false;
                else vm.Query = "";
                e.Handled = true;
                break;
        }
    }

    private void Suggestion_Click(object sender, MouseButtonEventArgs e)
    {
        var item = DragReorder.FindContainer(e.OriginalSource as DependencyObject);
        if (item?.DataContext is SpotifySuggestion s && Vm != null) _ = Vm.PickAsync(s);
    }

    // The dropdown never takes focus, so leaving the search field means the user moved on.
    private void Box_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm != null) Vm.ShowSuggestions = false;
    }

    private void Box_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm is { } vm && vm.Suggestions.Count > 0 && vm.Query.Trim().Length > 0 && !vm.IsResultsMode) vm.ShowSuggestions = true;
    }
}
