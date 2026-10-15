using Avalonia.Controls.Templates;
using Avalonia.Controls;
using SimpleBikeTrainer.Desktop.ViewModels;
using SimpleBikeTrainer.Desktop.Views;

namespace SimpleBikeTrainer.Desktop;

/// <summary>Maps each page's view model to its view.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        HomeViewModel => new HomeView(),
        RideViewModel => new RideView(),
        SummaryViewModel => new SummaryView(),
        SettingsViewModel => new SettingsView(),
        HistoryViewModel => new HistoryView(),
        CalibrationViewModel => new CalibrationView(),
        _ => new TextBlock { Text = $"No view for {param?.GetType().Name}" },
    };

    public bool Match(object? data) => data is ViewModelBase;
}
