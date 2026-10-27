using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.App.Navigation;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Core.Launching;
using SubReel.Launcher.App.Services;
using System.Windows.Threading;

namespace SubReel.Launcher.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IGameAuthService _auth;
    private readonly ILaunchOrchestrator _launchOrchestrator;
    private readonly ILocalizationService _localization;
    private readonly DispatcherTimer _statusResetTimer;
    private string? _gameStatus;
    private LaunchStatus? _lastLaunchStatus;

    public ObservableCollection<LauncherAccount> Accounts { get; } = [];

    public ObservableCollection<NavigationItem> NavigationItems =>
        _navigation.Items;

    public object? CurrentViewModel =>
        _navigation.CurrentViewModel;

    public string PageTitle =>
        _navigation.CurrentItem is { } item ? _localization.Get($"Nav.{item.Key}", item.Title) : "SubReel Launcher";

    public string StatusText => _gameStatus ?? string.Concat(_localization.Get("Shell.Section", "Раздел"), ": ", PageTitle);
    public bool HasCancelableLaunch => _launchOrchestrator.ActiveLaunches.Any(status => status.State != LaunchState.Running);

    public string AccountText => _auth.ActiveAccount is { } account
        ? account.Type == LauncherAccountType.Offline
            ? string.Concat(account.Username, " · ", _localization.Get("Shell.OfflineNoLicense", "офлайн · без лицензии"))
            : account.Username
        : _localization.Get("Shell.AddAccount", "Добавить аккаунт");
    public LauncherAccount? ActiveAccount => _auth.ActiveAccount;
    public bool CanSignIn => true;
    public bool CanSignInMicrosoft => true;

    public MainViewModel(INavigationService navigation, IGameAuthService auth, ILaunchOrchestrator launchOrchestrator,
        ILocalizationService localization)
    {
        _navigation = navigation;
        _auth = auth;
        _launchOrchestrator = launchOrchestrator;
        _localization = localization;
        _statusResetTimer = new DispatcherTimer();
        _statusResetTimer.Tick += (_, _) =>
        {
            _statusResetTimer.Stop();
            _lastLaunchStatus = null;
            _gameStatus = BuildActiveGameStatus();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HasCancelableLaunch));
        };
        _launchOrchestrator.StatusChanged += OnLaunchStatusChanged;
        _navigation.PropertyChanged += OnNavigationPropertyChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        RefreshNavigationTitles();
        RefreshAccounts();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshNavigationTitles();
        _gameStatus = _launchOrchestrator.ActiveLaunches.Count > 1
            ? BuildActiveGameStatus()
            : _lastLaunchStatus is { } lastStatus
                ? FormatLaunchStatus(lastStatus)
                : BuildActiveGameStatus();
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(HasCancelableLaunch));
    }

    private void RefreshNavigationTitles()
    {
        for (var index = 0; index < _navigation.Items.Count; index++)
        {
            var item = _navigation.Items[index];
            _navigation.Items[index] = item with
            {
                Title = _localization.Get($"Nav.{item.Key}", item.Title),
                Description = _localization.Get($"Nav.{item.Key}.Description", item.Description)
            };
        }
    }

    private void OnLaunchStatusChanged(LaunchStatus status)
    {
        void Update()
        {
            _lastLaunchStatus = status;
            var isTerminal = status.State is LaunchState.Exited or LaunchState.Crashed or LaunchState.Canceled or LaunchState.Failed;
            if (isTerminal)
            {
                _gameStatus = FormatLaunchStatus(status);
                _statusResetTimer.Interval = status.State == LaunchState.Crashed
                    ? TimeSpan.FromSeconds(20)
                    : TimeSpan.FromSeconds(8);
                _statusResetTimer.Stop();
                _statusResetTimer.Start();
            }
            else
            {
                _statusResetTimer.Stop();
                _gameStatus = _launchOrchestrator.ActiveLaunches.Count > 1
                    ? BuildActiveGameStatus()
                    : FormatLaunchStatus(status);
            }
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HasCancelableLaunch));
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
            1 => string.Format(_localization.Culture, _localization.Get("Shell.ActiveGame", "Игра: {0} · {1}"), active.First().InstanceName, FormatLaunchMessage(active.First())),
            _ => string.Format(_localization.Culture, _localization.Get("Shell.ActiveGames", "Запущено игр: {0}"), active.Count)
        };
    }

    [RelayCommand]
    private void CancelActiveLaunch()
    {
        var launching = _launchOrchestrator.ActiveLaunches
            .FirstOrDefault(status => status.State != LaunchState.Running);
        if (launching is not null)
            _launchOrchestrator.CancelLaunch(launching.InstanceId);
    }

    private string FormatLaunchStatus(LaunchStatus status)
    {
        var message = FormatLaunchMessage(status);
        return status.State is LaunchState.Exited or LaunchState.Crashed or LaunchState.Canceled or LaunchState.Failed
            ? string.Format(_localization.Culture, _localization.Get("Shell.Launch.InstanceStatus", "{0}: {1}"), status.InstanceName, message)
            : string.Format(_localization.Culture, _localization.Get("Shell.ActiveGame", "Игра: {0} · {1}"), status.InstanceName, message);
    }

    private string FormatLaunchMessage(LaunchStatus status) => status.State switch
    {
        LaunchState.Preparing => string.Format(_localization.Culture, _localization.Get("Shell.Launch.Preparing", "Подготовка Minecraft {0}…"), ExtractVersion(status.Message)),
        LaunchState.InstallingLoader => _localization.Get("Shell.Launch.InstallingLoader", "Установка загрузчика Minecraft…"),
        LaunchState.Downloading => _localization.Get("Shell.Launch.Downloading", "Загрузка файлов Minecraft…"),
        LaunchState.Starting => _localization.Get("Shell.Launch.Starting", "Запуск Minecraft…"),
        LaunchState.Running => _localization.Get("Shell.Launch.Running", "Minecraft запущен."),
        LaunchState.Exited => _localization.Get("Shell.Launch.Exited", "Minecraft закрыт."),
        LaunchState.Canceled => _localization.Get("Shell.Launch.Canceled", "Запуск отменён."),
        LaunchState.Crashed when status.CrashReportPath is not null => string.Format(_localization.Culture,
            _localization.Get("Shell.Launch.CrashedReport", "Minecraft завершился с кодом {0}. Отчёт: {1}"),
            status.ExitCode, System.IO.Path.GetFileName(status.CrashReportPath)),
        LaunchState.Crashed => string.Format(_localization.Culture,
            _localization.Get("Shell.Launch.Crashed", "Minecraft завершился с кодом {0}."), status.ExitCode),
        LaunchState.Failed => string.Format(_localization.Culture,
            _localization.Get("Shell.Launch.Failed", "Не удалось запустить Minecraft: {0}"), status.Message),
        _ => status.Message
    };

    private static string ExtractVersion(string message)
    {
        const string prefix = "Подготовка Minecraft ";
        return message.StartsWith(prefix, StringComparison.Ordinal)
            ? message[prefix.Length..].TrimEnd('…')
            : message;
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
            System.Windows.MessageBox.Show(ex.Message, _localization.Get("Account.Title", "Аккаунт"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task AddOfflineAccountAsync()
    {
        var username = Microsoft.VisualBasic.Interaction.InputBox(
            _localization.Get("Account.OfflinePrompt", "Ник: 3–16 символов, латинские буквы, цифры и _."),
            _localization.Get("Account.AddOfflineTitle", "Добавить офлайн-аккаунт"), "Player");
        if (string.IsNullOrWhiteSpace(username)) return;
        try
        {
            await _auth.AddOfflineAccountAsync(username);
            RefreshAccounts();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, _localization.Get("Account.OfflineTitle", "Офлайн-аккаунт"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
            System.Windows.MessageBox.Show(ex.Message, _localization.Get("Account.Title", "Аккаунт"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task RemoveAccountAsync(LauncherAccount? account)
    {
        if (account is null) return;
        var answer = System.Windows.MessageBox.Show(
            string.Format(_localization.Culture, _localization.Get("Account.RemoveConfirm", "Удалить аккаунт {0} из лаунчера?"), account.Username),
            _localization.Get("Account.RemoveTitle", "Удаление аккаунта"),
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
            System.Windows.MessageBox.Show(ex.Message, _localization.Get("Account.Title", "Аккаунт"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        try { await _auth.SignOutAsync(); RefreshAccounts(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, _localization.Get("Account.Title", "Аккаунт"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
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
