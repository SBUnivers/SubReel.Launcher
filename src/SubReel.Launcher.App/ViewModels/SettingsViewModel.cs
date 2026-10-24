using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.App.Services;
using SubReel.Launcher.App.DesignSystem;
using SubReel.Launcher.Abstractions.Settings;
using SubReel.Launcher.Abstractions.Game;

namespace SubReel.Launcher.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly IWindowBackdropService _backdrop;
    private readonly IMotionService _motion;
    private readonly IGameAuthService _auth;

    public IReadOnlyList<string> BackdropOptions { get; } = Enum.GetNames<WindowBackdropMode>();

    [ObservableProperty]
    private string _microsoftClientId = string.Empty;

    [ObservableProperty]
    private string _selectedWindowBackdrop = "Mica";

    [ObservableProperty]
    private bool _reduceAnimations;

    public string CurrentVersion => _updates.CurrentVersion;
    public string UpdateSetupStatus => _updates.ConfigurationStatus;
    public bool CanCheckForUpdates => _updates.CanCheckForUpdates && !IsBusy;
    public bool HasAvailableUpdate => !string.IsNullOrEmpty(AvailableVersion);

    [ObservableProperty]
    private string _statusText = "Проверка ещё не запускалась.";

    [ObservableProperty]
    private string _availableVersion = string.Empty;

    [ObservableProperty]
    private int _downloadProgress;

    [ObservableProperty]
    private bool _isBusy;

    public SettingsViewModel(IUpdateService updates, ISettingsService settings, IWindowBackdropService backdrop, IMotionService motion, IGameAuthService auth)
    {
        _updates = updates;
        _settings = settings;
        _backdrop = backdrop;
        _motion = motion;
        _auth = auth;
        _microsoftClientId = auth.MicrosoftClientId;
        _selectedWindowBackdrop = backdrop.Mode.ToString();
        _reduceAnimations = motion.Level != MotionLevel.Full;
    }

    partial void OnReduceAnimationsChanged(bool value) => _ = SaveMotionAsync(value);

    private async Task SaveMotionAsync(bool value)
    {
        _motion.SetLevel(value ? MotionLevel.Reduced : MotionLevel.Full);
        System.Windows.Application.Current.Resources["MotionLevel"] = _motion.Level;
        try
        {
            var settings = await _settings.LoadAsync();
            settings.ReduceAnimations = value;
            await _settings.SaveAsync(settings);
        }
        catch (Exception ex) { StatusText = $"Не удалось сохранить настройку анимаций: {ex.Message}"; }
    }

    partial void OnSelectedWindowBackdropChanged(string value) => _ = SaveBackdropAsync(value);

    private async Task SaveBackdropAsync(string value)
    {
        if (!Enum.TryParse<WindowBackdropMode>(value, out var mode)) return;
        _backdrop.SetMode(mode);
        try
        {
            var settings = await _settings.LoadAsync();
            settings.WindowBackdrop = mode.ToString();
            await _settings.SaveAsync(settings);
        }
        catch (Exception ex) { StatusText = $"Не удалось сохранить оформление: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SaveMicrosoftClientIdAsync()
    {
        var clientId = MicrosoftClientId.Trim();
        if (clientId.Length > 0 && !Guid.TryParse(clientId, out _))
        {
            StatusText = "Client ID должен быть GUID. Проверь значение в регистрации приложения Microsoft.";
            return;
        }
        try
        {
            var settings = await _settings.LoadAsync();
            settings.MicrosoftClientId = clientId;
            await _settings.SaveAsync(settings);
            _auth.ConfigureMicrosoftClientId(clientId);
            MicrosoftClientId = _auth.MicrosoftClientId;
            StatusText = clientId.Length == 0
                ? "Microsoft-вход отключён. Client ID удалён из настроек."
                : "Client ID сохранён. Для игрового входа Microsoft приложению требуется одобрение Mojang.";
        }
        catch (Exception ex) { StatusText = $"Не удалось сохранить Client ID: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            OnPropertyChanged(nameof(CanCheckForUpdates));
            StatusText = "Проверяю обновления…";
            var version = await _updates.CheckForUpdatesAsync();
            AvailableVersion = version ?? string.Empty;
            OnPropertyChanged(nameof(HasAvailableUpdate));
            StatusText = version is null
                ? "Установлена последняя версия."
                : $"Доступна версия {version}.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanCheckForUpdates));
        }
    }

    [RelayCommand]
    private async Task DownloadAndRestartAsync()
    {
        if (IsBusy || string.IsNullOrEmpty(AvailableVersion)) return;
        try
        {
            IsBusy = true;
            OnPropertyChanged(nameof(CanCheckForUpdates));
            StatusText = $"Загружаю обновление {AvailableVersion}…";
            await _updates.DownloadAndRestartAsync(value =>
            {
                DownloadProgress = value;
                StatusText = $"Загружаю обновление: {value}%";
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось установить обновление: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanCheckForUpdates));
        }
    }
}
