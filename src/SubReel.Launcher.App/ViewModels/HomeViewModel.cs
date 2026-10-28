using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Abstractions.Settings;
using SubReel.Launcher.App.DesignSystem;
using SubReel.Launcher.App.Services;
using SubReel.Launcher.App.Navigation;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Launching;
using SubReel.Launcher.Core.Models;

namespace SubReel.Launcher.App.ViewModels;

public sealed record MinecraftVersionOption(string Id, bool IsSnapshot, bool IsLatest, string Channel = "release")
{
    public bool IsLegacy => Channel.Equals("old_alpha", StringComparison.OrdinalIgnoreCase) || Channel.Equals("old_beta", StringComparison.OrdinalIgnoreCase);
    public string ChannelLabel => Channel.ToUpperInvariant().Replace('_', ' ');
    public string Label => IsSnapshot ? $"{Id}  ·  snapshot" : IsLegacy ? $"{Id}  ·  {ChannelLabel}" : Id;
}

public sealed class MinecraftReleaseFamily : ObservableObject
{
    private readonly string _sourceName;
    private readonly string _sourceTagline;
    private string _name;
    private string _tagline;

    public string Id { get; }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string Accent { get; }
    public string Tagline { get => _tagline; private set => SetProperty(ref _tagline, value); }
    public IReadOnlyList<MinecraftVersionOption> Versions { get; }
    public Uri? ArtUri { get; }

    public MinecraftReleaseFamily(string id, string name, string accent, string tagline, IReadOnlyList<MinecraftVersionOption> versions, Uri? artUri = null)
    {
        Id = id;
        _sourceName = _name = name;
        Accent = accent;
        _sourceTagline = _tagline = tagline;
        Versions = versions;
        ArtUri = artUri;
    }

    public void ApplyLocalization(ILocalizationService localization)
    {
        Name = localization.Get($"Home.Family.{Id}.Title", _sourceName);
        Tagline = localization.Get($"Home.Family.{Id}.Tagline", _sourceTagline);
    }

    public string LatestVersion => Versions.FirstOrDefault(x => !x.IsSnapshot)?.Id ?? Versions.FirstOrDefault()?.Id ?? Id;
    public string LatestVersionLabel => $"Последняя версия {LatestVersion}";
}

public partial class HomeViewModel : ObservableObject
{
    private static readonly string[] LoaderIds = ["Vanilla", "Fabric", "Quilt", "NeoForge", "Forge"];
    private readonly IGameVersionCatalog _catalog;
    private readonly ILaunchOrchestrator _launchOrchestrator;
    private readonly IGameLoaderCatalog _loaderCatalog;
    private readonly IGameAuthService _auth;
    private readonly IThemeService _theme;
    private readonly ISettingsService _settings;
    private readonly InstanceService _instances;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly ILocalizationService _localization;
    private readonly ILauncherNewsService _news;
    private readonly INavigationService _navigation;
    private bool _restoringSelection;
    private CancellationTokenSource? _launchCancellation;

    public ObservableCollection<MinecraftReleaseFamily> Families { get; } = [];
    public ObservableCollection<MinecraftVersionOption> ExactVersions { get; } = [];
    public ObservableCollection<MinecraftInstance> Instances { get; } = [];
    public bool HasSelectedInstance => SelectedInstance is not null;
    public bool HasVersionFamilies => Families.Count > 0;
    public ObservableCollection<GameLoaderOption> LoaderOptions { get; } =
    [
        new GameLoaderOption("Vanilla", "Vanilla", null, true, "Доступно для выбранной версии.")
    ];

    [ObservableProperty] private MinecraftReleaseFamily? _selectedFamily;
    [ObservableProperty] private MinecraftInstance? _selectedInstance;
    [ObservableProperty] private string _selectedVersion = "";
    [ObservableProperty] private string _selectedLoader = "Vanilla";
    [ObservableProperty] private string _statusTextRaw = "Загружаю официальный список версий Minecraft…";
    [ObservableProperty] private string _versionCatalogStatusRaw = "Загружаю версии Minecraft…";
    [ObservableProperty] private string _loaderCatalogStatusRaw = "Выбери версию игры, чтобы проверить загрузчики.";
    [ObservableProperty] private string _selectedAccent = "#8875FF";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private double _launchProgress;
    [ObservableProperty] private LauncherRelease? _featuredNews;
    public string StatusText => LocalizeHomeStatus(StatusTextRaw);
    public bool IsSelectedInstanceRunning => SelectedInstance is { } instance &&
        _launchOrchestrator.ActiveLaunches.Any(status => status.InstanceId == instance.Id && status.State == LaunchState.Running);
    public bool CanPlay => !IsBusy && !IsSelectedInstanceRunning && !string.IsNullOrWhiteSpace(SelectedVersion);
    public string HeroTitle => SelectedInstance?.Name ?? SelectedFamily?.Name ?? _localization.Get("Home.HeroFallbackTitle", "Твой мир. Твои правила.");
    public string HeroSubtitle => SelectedInstance is { } instance
        ? $"{instance.MinecraftVersion} · {instance.LoaderType} · {instance.MemoryMb} MB"
        : SelectedFamily?.Tagline ?? _localization.Get("Home.HeroFallbackSubtitle", "Выбери сборку и продолжи приключение.");
    public Uri? HeroArtUri => SelectedFamily?.ArtUri;
    public string FeaturedNewsTitle => FeaturedNews?.Title ?? _localization.Get("Home.NewsFallbackTitle", "Обновления SubReel");
    public string FeaturedNewsDate => FeaturedNews?.DateLabel ?? _localization.Get("Home.NewsFallbackSubtitle", "Открыть ленту новостей");
    public string VersionCatalogStatus => LocalizeHomeStatus(VersionCatalogStatusRaw);
    public string LoaderCatalogStatus => LocalizeHomeStatus(LoaderCatalogStatusRaw);
    public string SelectedFamilyLatestVersionLabel => SelectedFamily is null
        ? string.Empty
        : string.Format(_localization.Culture, _localization.Get("Home.LatestVersionLabel", "Последняя версия {0}"), SelectedFamily.LatestVersion);
    public string SelectedVersionSummary => string.IsNullOrEmpty(SelectedVersion)
        ? _localization.Get("Home.SelectVersionHint", "Выбери версию слева.")
        : string.Format(_localization.Culture, _localization.Get("Home.VersionWillPrepare", "Будет подготовлена версия {0}"), SelectedVersion);

    partial void OnStatusTextRawChanged(string value) => OnPropertyChanged(nameof(StatusText));
    partial void OnVersionCatalogStatusRawChanged(string value) => OnPropertyChanged(nameof(VersionCatalogStatus));
    partial void OnLoaderCatalogStatusRawChanged(string value) => OnPropertyChanged(nameof(LoaderCatalogStatus));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanPlay));

    private string LocalizeHomeStatus(string text)
    {
        var key = text switch
        {
            "Загружаю официальный список версий Minecraft…" => "Home.Status.LoadingOfficialVersions",
            "Загружаю версии Minecraft…" => "Home.Status.LoadingVersions",
            "Выбери версию игры, чтобы проверить загрузчики." => "Home.Status.SelectVersionForLoaders",
            "Список версий обновлён из манифеста Minecraft." => "Home.Status.VersionsUpdated",
            "Не удалось найти доступные релизы Minecraft." => "Home.Status.NoReleases",
            "Не удалось получить манифест; корректного локального кэша нет." => "Home.Status.ManifestUnavailable",
            "Совместимость загрузчиков проверена." => "Home.Status.LoadersChecked",
            "Доступно для выбранной версии." => "Home.Status.LoaderAvailable",
            "Запуск отменён." => "Home.Status.LaunchCancelled",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(key)) return _localization.Get(key, text);
        if (text.StartsWith("Манифест Mojang обновлён ", StringComparison.Ordinal))
            return FormatHomeStatus("Home.Status.ManifestUpdated", text, text["Манифест Mojang обновлён ".Length..]);
        if (text.StartsWith("Локальный кэш версий · обновлён ", StringComparison.Ordinal))
            return FormatHomeStatus("Home.Status.CachedManifest", text, text["Локальный кэш версий · обновлён ".Length..]);
        if (text.StartsWith("Нет соединения с Mojang · локальный кэш от ", StringComparison.Ordinal))
            return FormatHomeStatus("Home.Status.OfflineManifest", text, text["Нет соединения с Mojang · локальный кэш от ".Length..]);
        if (text.StartsWith("Подготавливаю Minecraft ", StringComparison.Ordinal))
            return FormatHomeStatus("Home.Status.Preparing", text, text["Подготавливаю Minecraft ".Length..].TrimEnd('…'));
        if (text.StartsWith("Подготовка Minecraft ", StringComparison.Ordinal))
            return FormatHomeStatus("Home.Status.Preparing", text, text["Подготовка Minecraft ".Length..].TrimEnd('…'));
        if (text.StartsWith("Установка загрузчика: ", StringComparison.Ordinal))
            return FormatHomeStatus("Home.Status.InstallingLoader", text, text["Установка загрузчика: ".Length..]);
        if (text == "Загрузка файлов Minecraft…") return _localization.Get("Home.Status.DownloadingFiles", text);
        if (text == "Запуск Java и ожидание окна Minecraft…") return _localization.Get("Home.Status.StartingGame", text);
        if (text is "Minecraft запущен." or "Minecraft запущен") return _localization.Get("Home.Status.Running", text);
        if (text.StartsWith("Проверяю загрузчики для Minecraft ", StringComparison.Ordinal))
            return FormatHomeStatus("Home.Status.CheckingLoaders", text, text["Проверяю загрузчики для Minecraft ".Length..].TrimEnd('…'));
        if (text.StartsWith("Minecraft ", StringComparison.Ordinal) && text.Contains(" запущен (PID ", StringComparison.Ordinal))
        {
            var pidStart = text.IndexOf(" (PID ", StringComparison.Ordinal);
            if (pidStart > 9 && text.EndsWith(')'))
                return FormatHomeStatus("Home.Status.GameStarted", text, text[9..pidStart], text[(pidStart + 6)..^1]);
        }
        if (text.StartsWith("Не удалось загрузить манифест Minecraft: ", StringComparison.Ordinal))
            return FormatHomeStatus("Home.Status.ManifestLoadFailed", text, text["Не удалось загрузить манифест Minecraft: ".Length..]);
        if (text.StartsWith("Не удалось запустить игру: ", StringComparison.Ordinal))
            return FormatHomeStatus("Home.Status.LaunchFailed", text, text["Не удалось запустить игру: ".Length..]);
        if (text.StartsWith("Не удалось проверить загрузчики: ", StringComparison.Ordinal) && text.EndsWith(". Vanilla остаётся доступна.", StringComparison.Ordinal))
            return FormatHomeStatus("Home.Status.LoaderCheckFailed", text, text["Не удалось проверить загрузчики: ".Length..^". Vanilla остаётся доступна.".Length]);
        return text;
    }

    private string FormatHomeStatus(string key, string fallback, params object[] args) =>
        string.Format(_localization.Culture, _localization.Get(key, fallback), args);

    public HomeViewModel(IGameVersionCatalog catalog, IGameLoaderCatalog loaderCatalog, ILaunchOrchestrator launchOrchestrator, IGameAuthService auth, IThemeService theme, ISettingsService settings, InstanceService instances, ILogger<HomeViewModel> logger, ILocalizationService localization, ILauncherNewsService news, INavigationService navigation)
    {
        _catalog = catalog;
        _loaderCatalog = loaderCatalog;
        _launchOrchestrator = launchOrchestrator;
        _auth = auth;
        _theme = theme;
        _settings = settings;
        _instances = instances;
        _logger = logger;
        _localization = localization;
        _news = news;
        _navigation = navigation;
        Families.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVersionFamilies));
        var orchestrator = launchOrchestrator;
        var weakViewModel = new WeakReference<HomeViewModel>(this);
        Action<LaunchStatus> statusHandler = null!;
        statusHandler = status =>
        {
            if (weakViewModel.TryGetTarget(out var viewModel))
                viewModel.OnLaunchStatusChanged(status);
            else if (statusHandler is not null)
                orchestrator.StatusChanged -= statusHandler;
        };
        orchestrator.StatusChanged += statusHandler;
        _localization.LanguageChanged += (_, _) =>
        {
            foreach (var family in Families)
                family.ApplyLocalization(_localization);
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(VersionCatalogStatus));
            OnPropertyChanged(nameof(LoaderCatalogStatus));
            OnPropertyChanged(nameof(SelectedVersionSummary));
            OnPropertyChanged(nameof(SelectedFamilyLatestVersionLabel));
            OnPropertyChanged(nameof(HeroTitle));
            OnPropertyChanged(nameof(HeroSubtitle));
            OnPropertyChanged(nameof(FeaturedNewsTitle));
            OnPropertyChanged(nameof(FeaturedNewsDate));
            UpdateManifestStatus();
        };
        _ = LoadVersionsAsync();
        _ = LoadFeaturedNewsAsync();
    }

    partial void OnSelectedFamilyChanged(MinecraftReleaseFamily? value)
    {
        OnPropertyChanged(nameof(SelectedFamilyLatestVersionLabel));
        OnPropertyChanged(nameof(HeroTitle));
        OnPropertyChanged(nameof(HeroSubtitle));
        OnPropertyChanged(nameof(HeroArtUri));
        ExactVersions.Clear();
        if (value is not null)
        {
            foreach (var version in value.Versions) ExactVersions.Add(version);
            SelectedVersion = value.LatestVersion;
            SelectedAccent = value.Accent;
            if (ColorConverter.ConvertFromString(value.Accent) is Color accent)
                _theme.SetAccent(accent);
        }
        OnPropertyChanged(nameof(SelectedFamily));
        QueueSaveSelection();
    }

    partial void OnSelectedVersionChanged(string value)
    {
        OnPropertyChanged(nameof(CanPlay));
        if (!_restoringSelection && SelectedInstance is not null &&
            !string.Equals(SelectedInstance.MinecraftVersion, value, StringComparison.OrdinalIgnoreCase))
            SelectedInstance = null;
        OnPropertyChanged(nameof(SelectedVersionSummary));
        QueueSaveSelection();
        if (!string.IsNullOrWhiteSpace(value)) _ = LoadLoaderOptionsAsync(value);
    }

    partial void OnSelectedLoaderChanged(string value)
    {
        if (!_restoringSelection && SelectedInstance is not null &&
            !SelectedInstance.LoaderType.ToString().Equals(value, StringComparison.OrdinalIgnoreCase))
            SelectedInstance = null;
        OnPropertyChanged(nameof(SelectedLoaderVersion));
        QueueSaveSelection();
    }

    partial void OnSelectedInstanceChanged(MinecraftInstance? value)
    {
        OnPropertyChanged(nameof(HasSelectedInstance));
        OnPropertyChanged(nameof(IsSelectedInstanceRunning));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(HeroTitle));
        OnPropertyChanged(nameof(HeroSubtitle));
        if (value is null) return;

        var wasRestoringSelection = _restoringSelection;
        _restoringSelection = true;
        try
        {
            var matchingFamily = Families.FirstOrDefault(family =>
                family.Versions.Any(version => version.Id == value.MinecraftVersion));
            if (matchingFamily is not null && SelectedFamily?.Id != matchingFamily.Id)
                SelectedFamily = matchingFamily;
            if (!ExactVersions.Any(version => version.Id == value.MinecraftVersion))
                ExactVersions.Insert(0, new MinecraftVersionOption(value.MinecraftVersion, false, false));
            SelectedVersion = value.MinecraftVersion;
            SelectedLoader = value.LoaderType.ToString();
        }
        finally
        {
            _restoringSelection = wasRestoringSelection;
        }

        _ = LoadLoaderOptionsAsync(value.MinecraftVersion);
    }

    partial void OnFeaturedNewsChanged(LauncherRelease? value)
    {
        OnPropertyChanged(nameof(FeaturedNewsTitle));
        OnPropertyChanged(nameof(FeaturedNewsDate));
    }

    private async Task LoadFeaturedNewsAsync()
    {
        if (!_news.IsConfigured) return;
        try
        {
            var items = await _news.GetLatestAsync();
            FeaturedNews = items.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Home news teaser is unavailable; the main page remains usable");
        }
    }

    [RelayCommand]
    private void OpenNews() => _navigation.NavigateTo("news");

    [RelayCommand]
    private void OpenInstances() => _navigation.NavigateTo("instances");

    [RelayCommand]
    private void OpenVersionPicker()
    {
        if (SelectedFamily is not null) SelectFamily(SelectedFamily);
    }

    private void OnLaunchStatusChanged(LaunchStatus status)
    {
        if (SelectedInstance?.Id != status.InstanceId) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            OnPropertyChanged(nameof(IsSelectedInstanceRunning));
            OnPropertyChanged(nameof(CanPlay));
        }));
    }

    private void UpdateManifestStatus()
    {
        var cultureName = _localization.Get("News.DateCulture", _localization.Language);
        var dateFormat = _localization.Get("Home.ManifestDateFormat", "dd.MM.yyyy HH:mm");
        var culture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
        var updatedAt = _catalog.LastManifestUpdate?.ToLocalTime().ToString(dateFormat, culture);
        var unknownDate = _localization.Get("Home.Status.UnknownDate", "неизвестной даты");
        var now = _localization.Get("Home.Status.Now", "сейчас");
        VersionCatalogStatusRaw = _catalog.IsOfflineFallback
            ? $"Нет соединения с Mojang · локальный кэш от {updatedAt ?? unknownDate}"
            : _catalog.IsUsingCachedManifest
                ? $"Локальный кэш версий · обновлён {updatedAt ?? unknownDate}"
                : $"Манифест Mojang обновлён {updatedAt ?? now}";
    }
    [RelayCommand]
    private Task RefreshVersionsAsync() => LoadVersionsAsync(forceRefresh: true);

    private async Task LoadVersionsAsync(bool forceRefresh = false)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            _restoringSelection = true;
            var settings = await _settings.LoadAsync();
            var savedInstances = await _instances.GetAllAsync();
            Instances.Clear();
            foreach (var instance in savedInstances) Instances.Add(instance);
            var presentations = await LoadVersionGroupPresentationsAsync();
            var versions = forceRefresh
                ? await _catalog.RefreshVersionsAsync()
                : await _catalog.GetVersionsAsync();
            UpdateManifestStatus();
            var visibleVersions = versions.Where(x =>
                x.Type.Equals("release", StringComparison.OrdinalIgnoreCase) ||
                (settings.IncludeSnapshots && x.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase)) ||
                (settings.IncludeLegacyVersions && IsLegacyChannel(x.Type)));
            foreach (var group in visibleVersions.GroupBy(x => GroupKey(x.Type, x.Id))
                         .OrderBy(g => presentations.TryGetValue(g.Key, out var presentation) ? presentation.Order : int.MaxValue)
                         .ThenByDescending(g => VersionSortKey(g.Key)))
            {
                var key = group.Key;
                var groupChannel = group.First().Type;
                var details = presentations.TryGetValue(key, out var known)
                    ? known
                    : new VersionGroupPresentation(key switch
                        {
                            "snapshots" => "Снапшоты",
                            "legacy-alpha" => "Старые Alpha",
                            "legacy-beta" => "Старые Beta",
                            _ => key
                        },
                        key == "snapshots" ? "ТЕСТОВЫЕ ВЕРСИИ" : IsLegacyChannel(groupChannel) ? "ЭКСПЕРИМЕНТАЛЬНЫЕ ВЕРСИИ" : "MINECRAFT UPDATE",
                        null, "#8875FF", int.MaxValue);
                var ordered = group.OrderByDescending(x => x.ReleaseTime).Take(30).ToArray();
                var options = ordered.Select((x, index) => new MinecraftVersionOption(
                        x.Id,
                        x.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase),
                        index == 0 && x.Type.Equals("release", StringComparison.OrdinalIgnoreCase),
                        x.Type))
                    .ToArray();
                if (options.Length > 0)
                {
                    var family = new MinecraftReleaseFamily(key, details.Title, details.Accent, details.Subtitle, options, ResolveArtUri(details.Art));
                    family.ApplyLocalization(_localization);
                    Families.Add(family);
                }
            }
            var savedInstance = settings.LastPlayedInstanceId is { } lastPlayedId
                ? savedInstances.FirstOrDefault(x => x.Id == lastPlayedId)
                : null;
            var savedInstanceFamily = savedInstance is null
                ? null
                : Families.FirstOrDefault(family => family.Versions.Any(version => version.Id == savedInstance.MinecraftVersion));
            SelectedFamily = savedInstanceFamily
                ?? Families.FirstOrDefault(x => x.Id == settings.LastSelectedVersionGroup)
                ?? Families.FirstOrDefault();
            if (savedInstance is not null)
                SelectedInstance = savedInstance;
            else if (SelectedFamily is not null && ExactVersions.Any(x => x.Id == settings.LastSelectedMinecraftVersion))
                SelectedVersion = settings.LastSelectedMinecraftVersion!;
            if (savedInstance is not null && LoaderIds.Contains(savedInstance.LoaderType.ToString(), StringComparer.OrdinalIgnoreCase))
                SelectedLoader = LoaderIds.First(x => x.Equals(savedInstance.LoaderType.ToString(), StringComparison.OrdinalIgnoreCase));
            else if (LoaderIds.Contains(settings.LastSelectedLoader, StringComparer.OrdinalIgnoreCase))
                SelectedLoader = LoaderIds.First(x => x.Equals(settings.LastSelectedLoader, StringComparison.OrdinalIgnoreCase));
            StatusTextRaw = Families.Count == 0 ? "Не удалось найти доступные релизы Minecraft." : "Список версий обновлён из манифеста Minecraft.";
        }
        catch (Exception ex)
        {
            StatusTextRaw = $"Не удалось загрузить манифест Minecraft: {ex.Message}";
            VersionCatalogStatusRaw = "Не удалось получить манифест; корректного локального кэша нет.";
        }
        finally { _restoringSelection = false; IsBusy = false; }
    }

    [RelayCommand]
    private void SelectFamily(MinecraftReleaseFamily? family)
    {
        if (family is null) return;
        SelectedFamily = family;
        IsExpanded = true;
    }

    [RelayCommand]
    private void GoBack() => IsExpanded = false;

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(SelectedVersion)) return;
        try
        {
            IsBusy = true;
            LaunchProgress = 0;
            StatusTextRaw = $"Подготавливаю Minecraft {SelectedVersion}…";
            using var launchCancellation = new CancellationTokenSource();
            _launchCancellation = launchCancellation;
            var settings = await _settings.LoadAsync(launchCancellation.Token);
            if (!Enum.TryParse<MinecraftLoaderType>(SelectedLoader, true, out var loaderType))
                throw new InvalidOperationException($"Неизвестный загрузчик: {SelectedLoader}.");
            var loaderVersion = LoaderOptions.FirstOrDefault(x => x.Id == SelectedLoader)?.Version;
            var instances = await _instances.GetAllAsync(launchCancellation.Token);
            var instance = SelectedInstance is { } selectedInstance &&
                           selectedInstance.MinecraftVersion == SelectedVersion && selectedInstance.LoaderType == loaderType
                ? selectedInstance
                : instances.FirstOrDefault(x => x.MinecraftVersion == SelectedVersion && x.LoaderType == loaderType);
            if (instance is null)
            {
                instance = await _instances.CreateAsync(
                    $"Minecraft {SelectedVersion}", SelectedVersion, loaderType, loaderVersion,
                    settings.DefaultMemoryMb, launchCancellation.Token);
                Instances.Add(instance);
            }
            else if (loaderType == MinecraftLoaderType.Quilt && !string.IsNullOrWhiteSpace(loaderVersion) &&
                     !string.Equals(instance.LoaderVersion, loaderVersion, StringComparison.Ordinal))
            {
                // Existing instances may have been created when the catalog incorrectly selected
                // an old Quilt build. Keep the instance aligned with the currently recommended one.
                instance.LoaderVersion = loaderVersion;
                await _instances.UpdateAsync(instance, launchCancellation.Token);
            }
            SelectedInstance = instance;
            var session = await _auth.GetActiveSessionAsync(launchCancellation.Token);
            var playerName = _auth.ActiveAccount?.Username ?? session?.Username ?? "SubReelPlayer";
            var progress = new Progress<LaunchProgress>(value =>
            {
                // Progress<T> posts callbacks to the UI dispatcher. Ignore queued
                // updates after cancellation/completion so they cannot overwrite the final status.
                if (launchCancellation.IsCancellationRequested || !IsBusy) return;

                StatusTextRaw = value.Stage switch
                {
                    LaunchStage.Resolving => $"Подготовка Minecraft {SelectedVersion}…",
                    LaunchStage.InstallingLoader => $"Установка загрузчика: {SelectedLoader}",
                    LaunchStage.DownloadingFiles => "Загрузка файлов Minecraft…",
                    LaunchStage.StartingGame => "Запуск Java и ожидание окна Minecraft…",
                    LaunchStage.Running => "Minecraft запущен.",
                    _ => value.Message
                };
                LaunchProgress = value.Fraction * 100;
            });
            Process process = await _launchOrchestrator.LaunchAsync(
                instance, session, playerName, progress, launchCancellation.Token);
            LaunchProgress = 100;
            StatusTextRaw = $"Minecraft {SelectedVersion} запущен (PID {process.Id}).";
            _ = RecordPlayTimeAsync(process, instance);
        }
        catch (OperationCanceledException) { StatusTextRaw = "Запуск отменён."; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось запустить Minecraft {Version} с загрузчиком {Loader}", SelectedVersion, SelectedLoader);
            StatusTextRaw = $"Не удалось запустить игру: {DescribeFailure(ex)}";
        }
        finally { _launchCancellation = null; IsBusy = false; }
    }

    [RelayCommand]
    private void CancelLaunch() => _launchCancellation?.Cancel();

    private void QueueSaveSelection()
    {
        if (!_restoringSelection) _ = SaveSelectionAsync();
    }

    private static async Task<IReadOnlyDictionary<string, VersionGroupPresentation>> LoadVersionGroupPresentationsAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "content", "version-groups.json");
        if (!File.Exists(path)) return new Dictionary<string, VersionGroupPresentation>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, VersionGroupPresentation>>(
                       stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new Dictionary<string, VersionGroupPresentation>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new Dictionary<string, VersionGroupPresentation>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Uri? ResolveArtUri(string? art)
    {
        if (string.IsNullOrWhiteSpace(art)) return null;
        if (Uri.TryCreate(art, UriKind.Absolute, out var absolute))
            return absolute.Scheme == Uri.UriSchemeHttps ? absolute : null;

        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "content"));
        var fullPath = Path.GetFullPath(Path.Combine(contentRoot, art));
        if (!fullPath.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return null;
        return new Uri(fullPath);
    }

    private async Task SaveSelectionAsync()
    {
        try
        {
            await _settings.UpdateAsync(settings =>
            {
                settings.LastSelectedVersionGroup = SelectedFamily?.Id;
                settings.LastSelectedMinecraftVersion = SelectedVersion;
                settings.LastSelectedLoader = SelectedLoader;
            });
        }
        catch (Exception ex) { StatusTextRaw = $"Не удалось сохранить выбор версии: {ex.Message}"; }
    }

    private async Task LoadLoaderOptionsAsync(string minecraftVersion)
    {
        LoaderCatalogStatusRaw = $"Проверяю загрузчики для Minecraft {minecraftVersion}…";
        try
        {
            var options = await _loaderCatalog.GetLoadersAsync(minecraftVersion);
            if (!string.Equals(minecraftVersion, SelectedVersion, StringComparison.Ordinal)) return;

            LoaderOptions.Clear();
            foreach (var option in options) LoaderOptions.Add(option);
            KeepSelectedInstanceLoaderAvailable(minecraftVersion);
            if (!LoaderOptions.Any(x => x.Id == SelectedLoader && x.IsAvailable))
                SelectedLoader = "Vanilla";
            OnPropertyChanged(nameof(SelectedLoaderVersion));
            var unavailableOptions = options.Where(option => !option.IsAvailable).ToArray();
            LoaderCatalogStatusRaw = unavailableOptions.Length == 0
                ? "Совместимость загрузчиков проверена."
                : "Недоступные варианты: " + string.Join(" ", unavailableOptions.Select(option =>
                    $"{option.Name}: {option.AvailabilityMessage}"));
        }
        catch (Exception ex)
        {
            if (!string.Equals(minecraftVersion, SelectedVersion, StringComparison.Ordinal)) return;
            LoaderOptions.Clear();
            LoaderOptions.Add(new GameLoaderOption("Vanilla", "Vanilla", null, true, "Доступно для выбранной версии."));
            KeepSelectedInstanceLoaderAvailable(minecraftVersion);
            if (!LoaderOptions.Any(x => x.Id == SelectedLoader && x.IsAvailable))
                SelectedLoader = "Vanilla";
            LoaderCatalogStatusRaw = $"Не удалось проверить загрузчики: {ex.Message}. Vanilla остаётся доступна.";
        }
    }

    private void KeepSelectedInstanceLoaderAvailable(string minecraftVersion)
    {
        if (SelectedInstance is not { } instance ||
            !instance.MinecraftVersion.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase))
            return;

        var loaderId = instance.LoaderType.ToString();
        var option = LoaderOptions.FirstOrDefault(item => item.Id.Equals(loaderId, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            LoaderOptions.Add(new GameLoaderOption(loaderId, loaderId, instance.LoaderVersion, true,
                "Используется установленный загрузчик выбранной сборки."));
        }
        else if (!option.IsAvailable)
        {
            var index = LoaderOptions.IndexOf(option);
            LoaderOptions[index] = option with
            {
                IsAvailable = true,
                AvailabilityMessage = "Используется установленный загрузчик выбранной сборки."
            };
        }

        SelectedLoader = loaderId;
    }

    public string? SelectedLoaderVersion => LoaderOptions.FirstOrDefault(x => x.Id == SelectedLoader)?.Version;

    private async Task RecordPlayTimeAsync(Process process, MinecraftInstance instance)
    {
        try
        {
            await process.WaitForExitAsync();
            instance.LastPlayedAt = DateTimeOffset.UtcNow;
            await _instances.UpdateAsync(instance);
        }
        catch (Exception ex)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                StatusTextRaw = $"Игра закрылась, но не удалось сохранить время запуска: {ex.Message}");
        }
        finally { process.Dispose(); }
    }

    private static string FamilyKey(string id)
    {
        var parts = id.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var first) && int.TryParse(parts[1].Split('-')[0], out _))
            return first >= 20 ? parts[0] : $"{parts[0]}.{parts[1]}";
        return id;
    }

    private static string GroupKey(string channel, string id) => channel.ToLowerInvariant() switch
    {
        "snapshot" => "snapshots",
        "old_alpha" => "legacy-alpha",
        "old_beta" => "legacy-beta",
        _ => FamilyKey(id)
    };

    private static bool IsLegacyChannel(string channel) =>
        channel.Equals("old_alpha", StringComparison.OrdinalIgnoreCase) ||
        channel.Equals("old_beta", StringComparison.OrdinalIgnoreCase);

    private static double VersionSortKey(string value)
    {
        if (value == "snapshots") return double.MinValue;
        var parts=value.Split('.');
        return double.TryParse(parts[0],out var first) ? first * 1000 + (parts.Length > 1 && double.TryParse(parts[1],out var second) ? second : 0) : 0;
    }

    private static string DescribeFailure(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (messages.Count == 0 || !string.Equals(messages[^1], current.Message, StringComparison.Ordinal))
                messages.Add(current.Message);
        }
        return string.Join(" → ", messages);
    }

    private sealed record VersionGroupPresentation(string Title, string Subtitle, string? Art, string Accent, int Order);
}
