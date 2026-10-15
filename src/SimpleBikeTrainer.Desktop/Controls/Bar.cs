using Avalonia.Controls;
using Avalonia.Media;
using Avalonia;

namespace SimpleBikeTrainer.Desktop.Controls;

/// <summary>A plain horizontal bar filled to <see cref="Value"/> (0 to 1) with <see cref="Fill"/>, on a faint track.</summary>
public sealed class Bar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Bar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<Bar, IBrush?>(nameof(Fill));

    static Bar()
    {
        AffectsRender<Bar>(ValueProperty, FillProperty);
    }

    public Bar()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var h = Bounds.Height;
        var radius = h / 2;
        context.DrawRectangle(Palette.Find("InsetBrush", "#0F0F0F", this), null, new Rect(Bounds.Size), radius, radius);
        var width = Bounds.Width * Math.Clamp(Value, 0, 1);
        if (width > 0.5)
        {
            context.DrawRectangle(Fill ?? Palette.Find("PrimaryColor", "#E5E5E5", this), null, new Rect(0, 0, Math.Max(width, h), h), radius, radius);
        }
    }
}
