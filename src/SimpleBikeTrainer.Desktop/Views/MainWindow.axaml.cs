using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Desktop.ViewModels;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Desktop.Views;

public partial class MainWindow : ShadUI.Window
{
    public MainWindow()
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
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PickFile = PickFileAsync;
            }
        };
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
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Open_Title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.Open_Workouts) { Patterns = ["*.txt", "*.json"] },
                FilePickerFileTypes.All,
            ],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
