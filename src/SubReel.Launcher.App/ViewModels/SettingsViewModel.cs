using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.App.Services;
using SubReel.Launcher.App.DesignSystem;
using SubReel.Launcher.Abstractions.Settings;
using SubReel.Launcher.Abstractions.Modpacks;

namespace SubReel.Launcher.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly IWindowBackdropService _backdrop;
    private readonly IMotionService _motion;
    private readonly ITranslationKeyStore _translationKeys;
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
    private bool _isTranslationKeyConfigured;

    [ObservableProperty]
    private string _translationKeyStatus = "Ключ DeepL ещё не добавлен.";

    [ObservableProperty]
    private string _catalogLanguageStatus = "Русский выбран — каталог переводится автоматически при поиске.";

    public SettingsViewModel(IUpdateService updates, ISettingsService settings, IWindowBackdropService backdrop, IMotionService motion, ITranslationKeyStore translationKeys)
    {
        _updates = updates;
        _settings = settings;
        _backdrop = backdrop;
        _motion = motion;
        _translationKeys = translationKeys;
        _selectedWindowBackdrop = backdrop.Mode.ToString();
        _reduceAnimations = motion.Level != MotionLevel.Full;
        _ = LoadDisplaySettingsAsync();
        _ = LoadTranslationKeyStatusAsync();
    }

    private async Task LoadTranslationKeyStatusAsync()
    {
        try
        {
            IsTranslationKeyConfigured = !string.IsNullOrWhiteSpace(await _translationKeys.GetAsync());
            TranslationKeyStatus = IsTranslationKeyConfigured ? "Ключ хранится в защищённом виде на этом компьютере." : "Ключ DeepL ещё не добавлен.";
        }
        catch (Exception ex) { TranslationKeyStatus = $"Не удалось прочитать ключ переводчика: {ex.Message}"; }
    }

    public async Task SaveTranslationKeyAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            TranslationKeyStatus = "Сначала вставьте ключ DeepL API.";
            return;
        }
        try
        {
            await _translationKeys.SaveAsync(key);
            IsTranslationKeyConfigured = true;
            TranslationKeyStatus = "Ключ сохранён в защищённом виде на этом компьютере.";
        }
        catch (Exception ex) { TranslationKeyStatus = $"Не удалось сохранить ключ: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RemoveTranslationKeyAsync()
    {
        try
        {
            await _translationKeys.SaveAsync(null);
            IsTranslationKeyConfigured = false;
            TranslationKeyStatus = "Ключ переводчика удалён.";
        }
        catch (Exception ex) { TranslationKeyStatus = $"Не удалось удалить ключ: {ex.Message}"; }
    }

    private async Task LoadDisplaySettingsAsync()
    {
        try
        {
            var settings = await _settings.LoadAsync();
            IncludeSnapshots = settings.IncludeSnapshots;
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
            var settings = await _settings.LoadAsync();
            settings.Language = LanguageOptions.Any(option => option.Code == language) ? language : "ru-RU";
            await _settings.SaveAsync(settings);
            CatalogLanguageStatus = language == "ru-RU"
                ? "Каталог будет автоматически переводиться на русский при поиске."
                : "Для английского используется исходный текст каталога.";
        }
        catch (Exception ex) { CatalogLanguageStatus = $"Не удалось сохранить язык: {ex.Message}"; }
    }

    partial void OnIncludeSnapshotsChanged(bool value) => _ = SaveSnapshotsAsync(value);

    private async Task SaveSnapshotsAsync(bool value)
    {
        try
        {
            var settings = await _settings.LoadAsync();
            settings.IncludeSnapshots = value;
            await _settings.SaveAsync(settings);
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
