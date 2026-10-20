using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimpleBikeTrainer.Desktop.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Android's back gesture. False leaves the app.</summary>
    public virtual bool GoBack() => false;

    public virtual bool HandleKey(Key key) => false;

    /// <summary>Runs when another page replaces this one, or the app closes on it.</summary>
    public virtual void OnLeaving()
    {
    }

    /// <summary>Runs when the page comes on screen, including when the rider comes back to it.</summary>
    public virtual void OnShown()
    {
    }
}
