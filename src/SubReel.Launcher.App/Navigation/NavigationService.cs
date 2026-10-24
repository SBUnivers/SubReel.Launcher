using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SubReel.Launcher.App.ViewModels;

namespace SubReel.Launcher.App.Navigation;

public sealed class NavigationService : ObservableObject, INavigationService
{
    private readonly Dictionary<string, Func<object>> _factories = new();

    public ObservableCollection<NavigationItem> Items { get; } = [];

    private NavigationItem? _currentItem;

    public NavigationItem? CurrentItem
    {
        get => _currentItem;
        private set => SetProperty(ref _currentItem, value);
    }

    private object? _currentViewModel;

    public object? CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    public void Register(
        NavigationItem item,
        Func<object>? viewModelFactory = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (Items.Any(x => x.Key == item.Key))
        {
            throw new InvalidOperationException(
                $"Пункт навигации '{item.Key}' уже зарегистрирован.");
        }

        Items.Add(item);

        _factories.Add(
            item.Key,
            viewModelFactory ?? (() =>
                new SectionViewModel(
                    item.Title,
                    item.Icon,
                    item.Description)));
    }

    public bool NavigateTo(string key)
    {
        var item = Items.FirstOrDefault(x => x.Key == key);

        if (item is null)
            return false;

        var viewModel = _factories[key]();

        CurrentItem = item;
        CurrentViewModel = viewModel;

        return true;
    }
}