using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentUIIconsLike;

namespace SimpleBikeTrainer.Desktop.Controls;

/// <summary>
/// An icon followed by <see cref="Text"/>, for a button's content. Screen readers name a button by its content's
/// <see cref="ToString"/>, which is the text.
/// </summary>
public sealed class IconLabel : StackPanel
{
    public static readonly StyledProperty<FluentIconSymbol> SymbolProperty =
        AvaloniaProperty.Register<IconLabel, FluentIconSymbol>(nameof(Symbol));

    public static readonly StyledProperty<FluentIconSize> SizeProperty =
        AvaloniaProperty.Register<IconLabel, FluentIconSize>(nameof(Size), FluentIconSize.Size20);

    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<IconLabel, string?>(nameof(Text));

    private readonly FluentIcon _icon = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        _icon.Size = Size;
        Children.Add(_icon);
        Children.Add(_text);
    }

    public FluentIconSymbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public FluentIconSize Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public override string ToString() => Text ?? string.Empty;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SymbolProperty)
        {
            _icon.Symbol = Symbol;
        }
        else if (change.Property == SizeProperty)
        {
            _icon.Size = Size;
        }
        else if (change.Property == TextProperty)
        {
            _text.Text = Text;
        }
    }
}
