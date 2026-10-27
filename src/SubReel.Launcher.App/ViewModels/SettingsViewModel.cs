using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.App.Services;
using SubReel.Launcher.App.DesignSystem;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Abstractions.Settings;

namespace SubReel.Launcher.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly IWindowBackdropService _backdrop;
    private readonly IMotionService _motion;
    private readonly IPlatformHealthService _platformHealth;
    private readonly IDiagnosticBundleService _diagnosticBundle;
    private readonly IJavaRuntimeInspector _javaRuntimeInspector;
    private readonly ILocalizationService _localization;
    private bool _settingsLoaded;

    public IReadOnlyList<string> BackdropOptions { get; } = Enum.GetNames<WindowBackdropMode>();
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = [new("Русский", "ru-RU"), new("English", "en-US")];
    public IReadOnlyList<TextScaleOption> TextScaleOptions { get; } =
    [
        new("85%", 0.85), new("90%", 0.9), new("100%", 1), new("110%", 1.1), new("125%", 1.25)
    ];

    [ObservableProperty]
    private double _selectedTextScale = 1d;

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
    public string JavaDialogTitle => _localization.Get("Settings.JavaDialogTitle", "Выбрать Java runtime");
    public string JavaDialogFilter => _localization.Get("Settings.JavaDialogFilter", "Java runtime (java.exe; javaw.exe)|java.exe;javaw.exe|Все файлы (*.*)|*.*");
    public string UpdateSetupStatus
    {
        get
        {
            var status = _updates.ConfigurationStatus;
            var key = status switch
            {
                "Источник обновлений не настроен. Укажи Updates:FeedUrl после публикации релизов." => "Settings.Status.UpdateSourceMissing",
                "Автообновление готово." => "Settings.Status.AutoUpdateReady",
                "Обновления доступны в установленной версии лаунчера." => "Settings.Status.UpdateInstallRequired",
                _ => string.Empty
            };
            return string.IsNullOrEmpty(key) ? status : _localization.Get(key, status);
        }
    }
    public bool CanCheckForUpdates => _updates.CanCheckForUpdates && !IsBusy;
    public bool HasAvailableUpdate => !string.IsNullOrEmpty(AvailableVersion);

    [ObservableProperty]
    private string _statusTextRaw = "Проверка ещё не запускалась.";
    public string StatusText => LocalizeStatus(StatusTextRaw);
    partial void OnStatusTextRawChanged(string value) => OnPropertyChanged(nameof(StatusText));

    [ObservableProperty]
    private string _availableVersion = string.Empty;

    [ObservableProperty]
    private int _downloadProgress;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _catalogLanguageStatus = "Перевод каталога выполняется через SubReel Platform для вошедших пользователей.";

    [ObservableProperty]
    private string _platformHealthStatusRaw = "Проверка Platform ещё не запускалась.";
    public string PlatformHealthStatus => LocalizeStatus(PlatformHealthStatusRaw);
    partial void OnPlatformHealthStatusRawChanged(string value) => OnPropertyChanged(nameof(PlatformHealthStatus));

    [ObservableProperty]
    private bool _isCheckingPlatform;

    [ObservableProperty]
    private string _diagnosticStatusRaw = "Архив включает только журналы лаунчера после фильтрации секретов и email.";
    public string DiagnosticStatus => LocalizeStatus(DiagnosticStatusRaw);
    partial void OnDiagnosticStatusRawChanged(string value) => OnPropertyChanged(nameof(DiagnosticStatus));

    [ObservableProperty]
    private bool _isCreatingDiagnosticBundle;

    [ObservableProperty]
    private double _defaultMemoryMb = 4096;

    [ObservableProperty]
    private string _javaPathInput = string.Empty;

    [ObservableProperty]
    private string _launchSettingsStatusRaw = "Пустой путь Java означает автоматический выбор runtime.";
    public string LaunchSettingsStatus => LocalizeStatus(LaunchSettingsStatusRaw);
    partial void OnLaunchSettingsStatusRawChanged(string value) => OnPropertyChanged(nameof(LaunchSettingsStatus));

    public SettingsViewModel(IUpdateService updates, ISettingsService settings, IWindowBackdropService backdrop,
        IMotionService motion, IPlatformHealthService platformHealth, IDiagnosticBundleService diagnosticBundle,
        IJavaRuntimeInspector javaRuntimeInspector, ILocalizationService localization)
    {
        _updates = updates;
        _settings = settings;
        _backdrop = backdrop;
        _motion = motion;
        _platformHealth = platformHealth;
        _diagnosticBundle = diagnosticBundle;
        _javaRuntimeInspector = javaRuntimeInspector;
        _localization = localization;
        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(PlatformHealthStatus));
            OnPropertyChanged(nameof(DiagnosticStatus));
            OnPropertyChanged(nameof(LaunchSettingsStatus));
            OnPropertyChanged(nameof(UpdateSetupStatus));
            OnPropertyChanged(nameof(JavaDialogTitle));
            OnPropertyChanged(nameof(JavaDialogFilter));
            UpdateCatalogLanguageStatus(SelectedLanguage);
        };
        _selectedWindowBackdrop = backdrop.Mode.ToString();
        _reduceAnimations = motion.Level != MotionLevel.Full;
        _ = LoadDisplaySettingsAsync();
    }

    private string LocalizeStatus(string text)
    {
        var key = text switch
        {
            "Проверка ещё не запускалась." => "Settings.Status.NotStarted",
            "Проверка Platform ещё не запускалась." => "Settings.Status.PlatformNotStarted",
            "Архив включает только журналы лаунчера после фильтрации секретов и email." => "Settings.Status.DiagnosticInfo",
            "Пустой путь Java означает автоматический выбор runtime." => "Settings.Status.JavaAutoInfo",
            "Создаю архив диагностики…" => "Settings.Status.CreatingArchive",
            "Не удалось создать архив. Проверь доступ к папке логов лаунчера." => "Settings.Status.ArchiveFailed",
            "Открыта папка логов и архивов диагностики." => "Settings.Status.LogsOpened",
            "Не удалось открыть папку логов лаунчера." => "Settings.Status.LogsFailed",
            "Проверяю доступность Platform…" => "Settings.Status.CheckingPlatform",
            "Не удалось проверить SubReel Platform. Проверь адрес API и попробуй позже." => "Settings.Status.PlatformFailed",
            "Выбери от 512 МБ до 32 ГБ оперативной памяти." => "Settings.Status.InvalidMemory",
            "Файл Java не найден. Укажи путь к java.exe/javaw.exe или оставь поле пустым для автоопределения." => "Settings.Status.JavaNotFound",
            "Путь не задан: лаунчер выберет Java автоматически при запуске. Чтобы проверить runtime вручную, укажи путь к java.exe или javaw.exe." => "Settings.Status.JavaPathEmpty",
            "Сохранено. Java будет выбрана автоматически; RAM по умолчанию применяется к новым сборкам, созданным с Главной." => "Settings.Status.LaunchSavedAuto",
            "Сохранено. Общий runtime Java применяется к сборкам без собственного пути Java." => "Settings.Status.LaunchSavedJava",
            "Не удалось сохранить параметры запуска." => "Settings.Status.LaunchSaveFailed",
            "Установлена последняя версия." => "Settings.Status.UpToDate",
            "Проверяю обновления…" => "Settings.Status.CheckingUpdates",
            "Не удалось сохранить настройки запуска." => "Settings.Status.LaunchSaveFailed",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(key)) return _localization.Get(key, text);

        if (text.StartsWith("Java ", StringComparison.Ordinal) && text.Contains(" запускается. Нажми «Сохранить»", StringComparison.Ordinal))
        {
            var versionStart = text.IndexOf(" (", StringComparison.Ordinal);
            var versionEnd = text.IndexOf(") запускается.", versionStart, StringComparison.Ordinal);
            if (versionStart > 5 && versionEnd > versionStart)
                return FormatStatus("Settings.Status.JavaVerified", text, text[5..versionStart], text[(versionStart + 2)..versionEnd]);
        }
        if (text.StartsWith("Проверка SubReel Platform не прошла: HTTP ", StringComparison.Ordinal))
        {
            var details = text["Проверка SubReel Platform не прошла: HTTP ".Length..]
                .Replace(". Возможно, health endpoint ещё не развёрнут.", string.Empty, StringComparison.Ordinal);
            return FormatStatus("Settings.Status.PlatformHttpFailed", text, details);
        }
        if (text.StartsWith("Platform доступна (", StringComparison.Ordinal))
        {
            var timeEnd = text.IndexOf("). Новости: ", StringComparison.Ordinal);
            var newsEnd = text.IndexOf("; вход: ", StringComparison.Ordinal);
            var signInEnd = text.IndexOf("; CurseForge: ", StringComparison.Ordinal);
            var curseForgeEnd = text.IndexOf("; перевод: ", StringComparison.Ordinal);
            var translationEnd = text.IndexOf(". Проверено наличие настроек сервера", StringComparison.Ordinal);
            if (timeEnd > 0 && newsEnd > timeEnd && signInEnd > newsEnd && curseForgeEnd > signInEnd && translationEnd > curseForgeEnd)
            {
                var labels = new[]
                {
                    text[(timeEnd + "). Новости: ".Length)..newsEnd],
                    text[(newsEnd + "; вход: ".Length)..signInEnd],
                    text[(signInEnd + "; CurseForge: ".Length)..curseForgeEnd],
                    text[(curseForgeEnd + "; перевод: ".Length)..translationEnd]
                }.Select(value => value == "настроено"
                    ? _localization.Get("Settings.Status.Configured", "настроено")
                    : _localization.Get("Settings.Status.NotConfigured", "не настроено"));
                var arguments = new object[] { text["Platform доступна (".Length..timeEnd] }
                    .Concat(labels.Cast<object>()).ToArray();
                return FormatStatus("Settings.Status.PlatformAvailable", text, arguments);
            }
        }

        foreach (var (prefix, resourceKey) in new[]
        {
            ("Архив создан: ", "Settings.Status.ArchiveCreated"),
            ("Не удалось сохранить настройки версий: ", "Settings.Status.SaveFailed"),
            ("Не удалось сохранить масштаб текста: ", "Settings.Status.SaveFailed"),
            ("Не удалось сохранить настройку анимаций: ", "Settings.Status.SaveFailed"),
            ("Не удалось сохранить оформление: ", "Settings.Status.SaveFailed"),
            ("Не удалось сохранить язык: ", "Settings.Status.SaveFailed"),
            ("Не удалось установить обновление: ", "Settings.Status.UpdateFailed"),
            ("Проверка Java не пройдена: ", "Settings.Status.JavaCheckFailed"),
            ("Загружаю обновление ", "Settings.Status.DownloadingUpdate"),
            ("Доступна версия ", "Settings.Status.UpdateAvailable")
        })
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var detail = text[prefix.Length..];
            if (resourceKey == "Settings.Status.DownloadingUpdate" && detail.EndsWith('…')) detail = detail[..^1];
            if (resourceKey == "Settings.Status.UpdateAvailable" && detail.EndsWith('.')) detail = detail[..^1];
            return FormatStatus(resourceKey, text, detail);
        }
        if (text.StartsWith("Загружаю обновление: ", StringComparison.Ordinal) && text.EndsWith('%'))
            return FormatStatus("Settings.Status.DownloadProgress", text, text[21..]);
        return text;
    }

    private string FormatStatus(string key, string fallback, params object[] args) =>
        string.Format(_localization.Culture, _localization.Get(key, fallback), args);

    [RelayCommand]
    private async Task CreateDiagnosticBundleAsync()
    {
        if (IsCreatingDiagnosticBundle) return;
        IsCreatingDiagnosticBundle = true;
        DiagnosticStatusRaw = "Создаю архив диагностики…";
        try
        {
            var path = await _diagnosticBundle.CreateAsync();
            DiagnosticStatusRaw = $"Архив создан: {path}";
        }
        catch (Exception)
        {
            DiagnosticStatusRaw = "Не удалось создать архив. Проверь доступ к папке логов лаунчера.";
        }
        finally { IsCreatingDiagnosticBundle = false; }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            _diagnosticBundle.OpenLogsFolder();
            DiagnosticStatusRaw = "Открыта папка логов и архивов диагностики.";
        }
        catch (Exception)
        {
            DiagnosticStatusRaw = "Не удалось открыть папку логов лаунчера.";
        }
    }

    [RelayCommand]
    private async Task CheckPlatformHealthAsync()
    {
        if (IsCheckingPlatform) return;
        IsCheckingPlatform = true;
        PlatformHealthStatusRaw = "Проверяю доступность Platform…";
        try
        {
            var health = await _platformHealth.CheckAsync();
            var config = health.Configuration;
            PlatformHealthStatusRaw = $"Platform доступна ({health.CheckedAt.ToLocalTime():HH:mm:ss}). Новости: {Configured(config.PublicNews)}; вход: {Configured(config.LauncherSignIn)}; CurseForge: {Configured(config.CurseForgeCatalog)}; перевод: {Configured(config.CatalogTranslation)}. Проверено наличие настроек сервера, соединение с каждым сервисом отдельно не проверяется.";
        }
        catch (HttpRequestException ex)
        {
            PlatformHealthStatusRaw = ex.StatusCode is null
                ? "Не удалось связаться с SubReel Platform. Проверь интернет и адрес сайта."
                : $"Проверка SubReel Platform не прошла: HTTP {(int)ex.StatusCode}. Возможно, health endpoint ещё не развёрнут.";
        }
        catch (Exception)
        {
            PlatformHealthStatusRaw = "Не удалось проверить SubReel Platform. Проверь адрес API и попробуй позже.";
        }
        finally { IsCheckingPlatform = false; }

        static string Configured(bool value) => value ? "настроено" : "не настроено";
    }

    private async Task LoadDisplaySettingsAsync()
    {
        try
        {
            var settings = await _settings.LoadAsync();
            DefaultMemoryMb = Math.Clamp(settings.DefaultMemoryMb, 512, 32768);
            JavaPathInput = settings.JavaPath ?? string.Empty;
            SelectedTextScale = double.IsFinite(settings.TextScale) ? Math.Clamp(settings.TextScale, 0.85, 1.25) : 1d;
            FontScaleProvider.Instance.ScaleFactor = SelectedTextScale;
            IncludeSnapshots = settings.IncludeSnapshots;
            IncludeLegacyVersions = settings.IncludeLegacyVersions;
            SelectedLanguage = settings.Language;
            UpdateCatalogLanguageStatus(SelectedLanguage);
            _settingsLoaded = true;
        }
        catch (Exception ex) { StatusTextRaw = $"Не удалось загрузить настройки версий: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SaveLaunchSettingsAsync()
    {
        if (!double.IsFinite(DefaultMemoryMb) || DefaultMemoryMb is < 512 or > 32768)
        {
            LaunchSettingsStatusRaw = "Выбери от 512 МБ до 32 ГБ оперативной памяти.";
            return;
        }

        var javaPath = JavaPathInput.Trim();
        if (!string.IsNullOrEmpty(javaPath) && !File.Exists(javaPath))
        {
            LaunchSettingsStatusRaw = "Файл Java не найден. Укажи путь к java.exe/javaw.exe или оставь поле пустым для автоопределения.";
            return;
        }

        try
        {
            await _settings.UpdateAsync(settings =>
            {
                settings.DefaultMemoryMb = (int)Math.Round(DefaultMemoryMb / 512d) * 512;
                settings.JavaPath = string.IsNullOrWhiteSpace(javaPath) ? null : Path.GetFullPath(javaPath);
            });
            DefaultMemoryMb = Math.Clamp((int)Math.Round(DefaultMemoryMb / 512d) * 512, 512, 32768);
            LaunchSettingsStatusRaw = string.IsNullOrEmpty(javaPath)
                ? "Сохранено. Java будет выбрана автоматически; RAM по умолчанию применяется к новым сборкам, созданным с Главной."
                : "Сохранено. Общий runtime Java применяется к сборкам без собственного пути Java.";
        }
        catch (Exception)
        {
            LaunchSettingsStatusRaw = "Не удалось сохранить параметры запуска.";
        }
    }

    [RelayCommand]
    private async Task InspectJavaAsync()
    {
        var path = JavaPathInput.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            LaunchSettingsStatusRaw = "Путь не задан: лаунчер выберет Java автоматически при запуске. Чтобы проверить runtime вручную, укажи путь к java.exe или javaw.exe.";
            return;
        }

        try
        {
            var runtime = await _javaRuntimeInspector.InspectAsync(path);
            LaunchSettingsStatusRaw = $"Java {runtime.MajorVersion} ({runtime.VersionText}) запускается. Нажми «Сохранить», чтобы назначить её общей для сборок без собственного пути.";
        }
        catch (Exception ex)
        {
            LaunchSettingsStatusRaw = $"Проверка Java не пройдена: {ex.Message}";
        }
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        _localization.SetLanguage(value);
        if (_settingsLoaded) _ = SaveLanguageAsync(value);
    }

    partial void OnSelectedTextScaleChanged(double value)
    {
        var scale = double.IsFinite(value) ? Math.Clamp(value, 0.85, 1.25) : 1d;
        FontScaleProvider.Instance.ScaleFactor = scale;
        if (_settingsLoaded) _ = SaveTextScaleAsync(scale);
    }

    private async Task SaveTextScaleAsync(double scale)
    {
        try
        {
            await _settings.UpdateAsync(settings => settings.TextScale = scale);
        }
        catch (Exception ex) { StatusTextRaw = $"Не удалось сохранить масштаб текста: {ex.Message}"; }
    }

    private async Task SaveLanguageAsync(string language)
    {
        try
        {
            var selectedLanguage = LanguageOptions.Any(option => option.Code == language) ? language : "ru-RU";
            await _settings.UpdateAsync(settings => settings.Language = selectedLanguage);
            UpdateCatalogLanguageStatus(selectedLanguage);
        }
        catch (Exception ex)
        {
            CatalogLanguageStatus = string.Format(_localization.Culture,
                _localization.Get("Settings.Status.LanguageSaveFailed", "Не удалось сохранить язык: {0}"), ex.Message);
        }
    }

    private void UpdateCatalogLanguageStatus(string language)
    {
        var russian = language.Equals("ru-RU", StringComparison.OrdinalIgnoreCase);
        CatalogLanguageStatus = _localization.Get(
            russian ? "Settings.CatalogRussian" : "Settings.CatalogEnglish",
            russian ? "Каталог будет автоматически переводиться на русский при поиске." : "Для английского используется исходный текст каталога.");
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
        catch (Exception ex) { StatusTextRaw = $"Не удалось сохранить настройки версий: {ex.Message}"; }
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
        catch (Exception ex) { StatusTextRaw = $"Не удалось сохранить настройки версий: {ex.Message}"; }
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
        catch (Exception ex) { StatusTextRaw = $"Не удалось сохранить настройку анимаций: {ex.Message}"; }
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
        catch (Exception ex) { StatusTextRaw = $"Не удалось сохранить оформление: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            OnPropertyChanged(nameof(CanCheckForUpdates));
            StatusTextRaw = "Проверяю обновления…";
            var version = await _updates.CheckForUpdatesAsync();
            AvailableVersion = version ?? string.Empty;
            OnPropertyChanged(nameof(HasAvailableUpdate));
            StatusTextRaw = version is null
                ? "Установлена последняя версия."
                : $"Доступна версия {version}.";
        }
        catch (Exception ex)
        {
            StatusTextRaw = string.Format(_localization.Culture,
                _localization.Get("Settings.Status.UpdateCheckFailed", "Не удалось проверить обновления: {0}"), ex.Message);
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
            StatusTextRaw = $"Загружаю обновление {AvailableVersion}…";
            await _updates.DownloadAndRestartAsync(value =>
            {
                DownloadProgress = value;
                StatusTextRaw = $"Загружаю обновление: {value}%";
            });
        }
        catch (Exception ex)
        {
            StatusTextRaw = $"Не удалось установить обновление: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanCheckForUpdates));
        }
    }
}

public sealed record LanguageOption(string Name, string Code);
public sealed record TextScaleOption(string Label, double Factor);
