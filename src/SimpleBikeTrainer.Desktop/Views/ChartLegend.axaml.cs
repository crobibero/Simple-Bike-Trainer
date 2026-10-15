using Avalonia.Controls;
using Avalonia;

namespace SimpleBikeTrainer.Desktop.Views;

public partial class ChartLegend : UserControl
{
    /// <summary>Explain a route's elevation profile instead of workout steps.</summary>
    public static readonly StyledProperty<bool> IsRouteProperty = AvaloniaProperty.Register<ChartLegend, bool>(nameof(IsRoute));

    public ChartLegend() => InitializeComponent();

    public bool IsRoute
    {
        get => GetValue(IsRouteProperty);
        set => SetValue(IsRouteProperty, value);
    }
}
