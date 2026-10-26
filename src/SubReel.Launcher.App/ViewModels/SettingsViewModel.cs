using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.App.Services;
using SubReel.Launcher.App.DesignSystem;
using SubReel.Launcher.Abstractions.Settings;

namespace SubReel.Launcher.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly IWindowBackdropService _backdrop;
    private readonly IMotionService _motion;
    private bool _settingsLoaded;

    public IReadOnlyList<string> BackdropOptions { get; } = Enum.GetNames<WindowBackdropMode>();
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = [new("Русский", "ru-RU"), new("English", "en-US")];

    [ObservableProperty]
    private string _selectedWindowBackdrop = "Mica";

    [ObservableProperty]
    private bool _reduceAnimations;

    [ObservableProperty]
    private bool _includeSnapshots;

    [ObservableProperty]
    private bool _includeLegacyVersions;

    [ObservableProperty]
    private string _selectedLanguage = "ru-RU";

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

    [ObservableProperty]
    private string _catalogLanguageStatus = "Перевод каталога выполняется через SubReel Platform для вошедших пользователей.";

    public SettingsViewModel(IUpdateService updates, ISettingsService settings, IWindowBackdropService backdrop, IMotionService motion)
    {
        _updates = updates;
        _settings = settings;
        _backdrop = backdrop;
        _motion = motion;
        _selectedWindowBackdrop = backdrop.Mode.ToString();
        _reduceAnimations = motion.Level != MotionLevel.Full;
        _ = LoadDisplaySettingsAsync();
    }

    private async Task LoadDisplaySettingsAsync()
    {
        try
        {
            var settings = await _settings.LoadAsync();
            IncludeSnapshots = settings.IncludeSnapshots;
            IncludeLegacyVersions = settings.IncludeLegacyVersions;
            SelectedLanguage = settings.Language;
            _settingsLoaded = true;
        }
        catch (Exception ex) { StatusText = $"Не удалось загрузить настройки версий: {ex.Message}"; }
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        if (_settingsLoaded) _ = SaveLanguageAsync(value);
    }

    private async Task SaveLanguageAsync(string language)
    {
        try
        {
            var selectedLanguage = LanguageOptions.Any(option => option.Code == language) ? language : "ru-RU";
            await _settings.UpdateAsync(settings => settings.Language = selectedLanguage);
            CatalogLanguageStatus = language == "ru-RU"
                ? "Каталог будет автоматически переводиться на русский при поиске."
                : "Для английского используется исходный текст каталога.";
        }
        catch (Exception ex) { CatalogLanguageStatus = $"Не удалось сохранить язык: {ex.Message}"; }
    }

    partial void OnIncludeSnapshotsChanged(bool value)
    {
        if (_settingsLoaded) _ = SaveSnapshotsAsync(value);
    }

    private async Task SaveSnapshotsAsync(bool value)
    {
        try
        {
            await _settings.UpdateAsync(settings => settings.IncludeSnapshots = value);
        }
        catch (Exception ex) { StatusText = $"Не удалось сохранить настройки версий: {ex.Message}"; }
    }

    partial void OnIncludeLegacyVersionsChanged(bool value)
    {
        if (_settingsLoaded) _ = SaveLegacyVersionsAsync(value);
    }

    private async Task SaveLegacyVersionsAsync(bool value)
    {
        try
        {
            await _settings.UpdateAsync(settings => settings.IncludeLegacyVersions = value);
        }
        catch (Exception ex) { StatusText = $"Не удалось сохранить настройки версий: {ex.Message}"; }
    }

    partial void OnReduceAnimationsChanged(bool value) => _ = SaveMotionAsync(value);

    private async Task SaveMotionAsync(bool value)
    {
        _motion.SetLevel(value ? MotionLevel.Reduced : MotionLevel.Full);
        System.Windows.Application.Current.Resources["MotionLevel"] = _motion.Level;
        try
        {
            await _settings.UpdateAsync(settings => settings.ReduceAnimations = value);
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
            await _settings.UpdateAsync(settings => settings.WindowBackdrop = mode.ToString());
        }
        catch (Exception ex) { StatusText = $"Не удалось сохранить оформление: {ex.Message}"; }
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

public sealed record LanguageOption(string Name, string Code);
