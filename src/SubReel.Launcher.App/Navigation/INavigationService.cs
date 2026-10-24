using System.Collections.ObjectModel;
using System.ComponentModel;
using SubReel.Launcher.App.ViewModels;

namespace SubReel.Launcher.App.Navigation;

public interface INavigationService : INotifyPropertyChanged
{
    ObservableCollection<NavigationItem> Items { get; }

    NavigationItem? CurrentItem { get; }

    object? CurrentViewModel { get; }

    void Register(
        NavigationItem item,
        Func<object>? viewModelFactory = null);

    bool NavigateTo(string key);
}