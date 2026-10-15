using Avalonia.Controls;

namespace SimpleBikeTrainer.Desktop.Views;

public partial class RideView : UserControl
{
    public RideView()
    {
        InitializeComponent();
        Page.SizeChanged += (_, _) => FitStats();
    }

    /// <summary>A grid gives an Auto row all it asks for, so without this cap the stats push the step card's text out of its card.</summary>
    private void FitStats()
    {
        var rows = Page.RowDefinitions;
        var others = rows[0].ActualHeight + rows[1].ActualHeight + rows[2].MinHeight + rows[4].ActualHeight;
        Stats.MaxHeight = Math.Max(0, Page.Bounds.Height - others - Stats.Margin.Top - Stats.Margin.Bottom);
    }
}
