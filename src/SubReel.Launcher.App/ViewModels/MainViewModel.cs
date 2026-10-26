using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.App.Navigation;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Core.Launching;
using System.Windows.Threading;

namespace SubReel.Launcher.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IGameAuthService _auth;
    private readonly ILaunchOrchestrator _launchOrchestrator;
    private readonly DispatcherTimer _statusResetTimer;
    private string? _gameStatus;

    public ObservableCollection<LauncherAccount> Accounts { get; } = [];

    public ObservableCollection<NavigationItem> NavigationItems =>
        _navigation.Items;

    public object? CurrentViewModel =>
        _navigation.CurrentViewModel;

    public string PageTitle =>
        _navigation.CurrentItem?.Title ?? "SubReel Launcher";

    public string StatusText => _gameStatus ?? $"Раздел: {PageTitle}";

    public string AccountText => _auth.ActiveAccount is { } account
        ? account.Type == LauncherAccountType.Offline ? $"{account.Username} · офлайн · без лицензии" : account.Username
        : "Добавить аккаунт";
    public LauncherAccount? ActiveAccount => _auth.ActiveAccount;
    public bool CanSignIn => true;
    public bool CanSignInMicrosoft => true;

    public MainViewModel(INavigationService navigation, IGameAuthService auth, ILaunchOrchestrator launchOrchestrator)
    {
        _navigation = navigation;
        _auth = auth;
        _launchOrchestrator = launchOrchestrator;
        _statusResetTimer = new DispatcherTimer();
        _statusResetTimer.Tick += (_, _) =>
        {
            _statusResetTimer.Stop();
            _gameStatus = BuildActiveGameStatus();
            OnPropertyChanged(nameof(StatusText));
        };
        _launchOrchestrator.StatusChanged += OnLaunchStatusChanged;
        _navigation.PropertyChanged += OnNavigationPropertyChanged;
        RefreshAccounts();
    }

    private void OnLaunchStatusChanged(LaunchStatus status)
    {
        void Update()
        {
            var isTerminal = status.State is LaunchState.Exited or LaunchState.Crashed or LaunchState.Canceled or LaunchState.Failed;
            if (isTerminal)
            {
                _gameStatus = status.CrashReportPath is null
                    ? $"{status.InstanceName}: {status.Message}"
                    : $"{status.InstanceName}: {status.Message} · {status.CrashReportPath}";
                _statusResetTimer.Interval = status.State == LaunchState.Crashed
                    ? TimeSpan.FromSeconds(20)
                    : TimeSpan.FromSeconds(8);
                _statusResetTimer.Stop();
                _statusResetTimer.Start();
            }
            else
            {
                _statusResetTimer.Stop();
                _gameStatus = BuildActiveGameStatus();
            }
            OnPropertyChanged(nameof(StatusText));
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Update();
        else dispatcher.BeginInvoke(Update);
    }

    private string? BuildActiveGameStatus()
    {
        var active = _launchOrchestrator.ActiveLaunches;
        return active.Count switch
        {
            0 => null,
            1 => $"Игра: {active.First().InstanceName} · {active.First().Message}",
            _ => $"Запущено игр: {active.Count}"
        };
    }

    public async Task RestoreAccountAsync()
    {
        await _auth.TryRestoreSessionAsync();
        RefreshAccounts();
    }

    [RelayCommand]
    private async Task AddMicrosoftAccountAsync()
    {
        try
        {
            await _auth.SignInAsync();
            RefreshAccounts();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Microsoft", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task AddOfflineAccountAsync()
    {
        var username = Microsoft.VisualBasic.Interaction.InputBox(
            "Ник: 3–16 символов, латинские буквы, цифры и _.", "Добавить офлайн-аккаунт", "Player");
        if (string.IsNullOrWhiteSpace(username)) return;
        try
        {
            await _auth.AddOfflineAccountAsync(username);
            RefreshAccounts();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Офлайн-аккаунт", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task ActivateAccountAsync(LauncherAccount? account)
    {
        if (account is null) return;
        try
        {
            await _auth.ActivateAccountAsync(account.Id);
            RefreshAccounts();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Аккаунт", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task RemoveAccountAsync(LauncherAccount? account)
    {
        if (account is null) return;
        var answer = System.Windows.MessageBox.Show(
            $"Удалить аккаунт {account.Username} из лаунчера?",
            "Удаление аккаунта",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        try
        {
            await _auth.RemoveAccountAsync(account.Id);
            RefreshAccounts();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Аккаунт", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        try { await _auth.SignOutAsync(); RefreshAccounts(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Аккаунт", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
    }

    private void RefreshAccounts()
    {
        Accounts.Clear();
        foreach (var account in _auth.Accounts) Accounts.Add(account);
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(ActiveAccount));
    }

    private void OnNavigationPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(INavigationService.CurrentItem))
        {
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(StatusText));
        }

        if (e.PropertyName == nameof(INavigationService.CurrentViewModel))
        {
            OnPropertyChanged(nameof(CurrentViewModel));
        }
    }

    [RelayCommand]
    private void Navigate(string key)
    {
        _navigation.NavigateTo(key);
    }
}
