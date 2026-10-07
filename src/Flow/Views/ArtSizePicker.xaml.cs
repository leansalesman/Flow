using System.Windows;
using System.Windows.Controls;

namespace Flow.Views;

/// <summary>Small / Medium / Large artwork size selector. Bind <see cref="Value"/> two-way.</summary>
public partial class ArtSizePicker : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ArtSizePicker),
        new FrameworkPropertyMetadata("Medium", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty GroupNameProperty = DependencyProperty.Register(
        nameof(GroupName), typeof(string), typeof(ArtSizePicker), new PropertyMetadata("ArtSize"));

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>Radio group name; give each picker on screen its own.</summary>
    public string GroupName { get => (string)GetValue(GroupNameProperty); set => SetValue(GroupNameProperty, value); }

    public ArtSizePicker() => InitializeComponent();
}
