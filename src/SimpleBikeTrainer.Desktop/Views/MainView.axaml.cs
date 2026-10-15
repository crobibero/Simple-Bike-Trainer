using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Desktop.ViewModels;

namespace SimpleBikeTrainer.Desktop.Views;

public partial class MainView : UserControl
{
    /// <summary>The smallest size the pages are laid out for, the desktop window's minimum.</summary>
    internal static readonly Size LayoutSize = new(1100, 720);

    private IInsetsManager? _insets;
    private TopLevel? _topLevel;

    public MainView()
    {
        InitializeComponent();
        HelpBackdrop.PointerPressed += (_, e) =>
        {
            // Closes on a click on the dimmed area itself; clicks inside the panel are ignored.
            if (e.Source == HelpBackdrop && DataContext is MainWindowViewModel vm)
            {
                vm.IsHelpOpen = false;
            }
        };
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PickFile = PickFileAsync;
            }
        };
        SizeChanged += (_, e) => FitTo(e.NewSize);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is null)
        {
            return;
        }

        // Only Android's back gesture: on the desktop, Alt+Left in a settings field would throw the edits away.
        if (OperatingSystem.IsAndroid())
        {
            _topLevel.BackRequested += OnBackRequested;
        }

        // Android draws the app under the status and navigation bars; the padding keeps the pages clear of them.
        _insets = _topLevel.InsetsManager;
        if (_insets is not null)
        {
            _insets.SafeAreaChanged += OnSafeAreaChanged;
            Padding = _insets.SafeAreaPadding;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.BackRequested -= OnBackRequested;
            _topLevel = null;
        }

        if (_insets is not null)
        {
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
            _insets = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e)
    {
        Padding = e.SafeAreaPadding;
        FitTo(Bounds.Size);
    }

    /// <summary>Android's back gesture: back to the home page, or out of the app from there.</summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            e.Handled = vm.GoBack();
        }
    }

    private void FitTo(Size size)
    {
        var available = size.Deflate(Padding);
        var scale = Math.Min(1, Math.Min(available.Width / LayoutSize.Width, available.Height / LayoutSize.Height));
        Fit.LayoutTransform = scale is > 0 and < 1 ? new ScaleTransform(scale, scale) : null;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // Typing in a text box is not a ride command.
        if (e.Source is TextBox || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        e.Handled = vm.HandleKey(e.Key);
    }

    private async Task<string?> PickFileAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Open_Title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.Open_Workouts) { Patterns = ["*.txt", "*.json"], MimeTypes = ["text/plain", "application/json"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0)
        {
            return null;
        }

        return files[0].TryGetLocalPath() ?? await CopyToTempAsync(files[0]);
    }

    /// <summary>Android's picker hands out content URIs, not paths: the workout is read from a copy.</summary>
    private static async Task<string> CopyToTempAsync(IStorageFile file)
    {
        var folder = Path.Combine(Path.GetTempPath(), "picked");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileName(file.Name));
        await using var source = await file.OpenReadAsync();
        await using var target = File.Create(path);
        await source.CopyToAsync(target);
        return path;
    }
}
