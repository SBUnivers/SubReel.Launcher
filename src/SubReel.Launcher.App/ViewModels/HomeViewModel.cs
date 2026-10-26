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

public sealed record MinecraftReleaseFamily(string Id, string Name, string Accent, string Tagline, IReadOnlyList<MinecraftVersionOption> Versions, Uri? ArtUri = null)
{
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
    private bool _restoringSelection;
    private CancellationTokenSource? _launchCancellation;

    public ObservableCollection<MinecraftReleaseFamily> Families { get; } = [];
    public ObservableCollection<MinecraftVersionOption> ExactVersions { get; } = [];
    public ObservableCollection<MinecraftInstance> Instances { get; } = [];
    public ObservableCollection<GameLoaderOption> LoaderOptions { get; } =
    [
        new GameLoaderOption("Vanilla", "Vanilla", null, true, "Доступно для выбранной версии.")
    ];

    [ObservableProperty] private MinecraftReleaseFamily? _selectedFamily;
    [ObservableProperty] private MinecraftInstance? _selectedInstance;
    [ObservableProperty] private string _selectedVersion = "";
    [ObservableProperty] private string _selectedLoader = "Vanilla";
    [ObservableProperty] private string _statusText = "Загружаю официальный список версий Minecraft…";
    [ObservableProperty] private string _versionCatalogStatus = "Загружаю версии Minecraft…";
    [ObservableProperty] private string _loaderCatalogStatus = "Выбери версию игры, чтобы проверить загрузчики.";
    [ObservableProperty] private string _selectedAccent = "#8875FF";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private double _launchProgress;
    public string SelectedVersionSummary => string.IsNullOrEmpty(SelectedVersion) ? "Выбери версию слева." : $"Будет подготовлена версия {SelectedVersion}";

    public HomeViewModel(IGameVersionCatalog catalog, IGameLoaderCatalog loaderCatalog, ILaunchOrchestrator launchOrchestrator, IGameAuthService auth, IThemeService theme, ISettingsService settings, InstanceService instances, ILogger<HomeViewModel> logger)
    {
        _catalog = catalog;
        _loaderCatalog = loaderCatalog;
        _launchOrchestrator = launchOrchestrator;
        _auth = auth;
        _theme = theme;
        _settings = settings;
        _instances = instances;
        _logger = logger;
        _ = LoadVersionsAsync();
    }

    partial void OnSelectedFamilyChanged(MinecraftReleaseFamily? value)
    {
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
            var updatedAt = _catalog.LastManifestUpdate?.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
            VersionCatalogStatus = _catalog.IsOfflineFallback
                ? $"Нет соединения с Mojang · локальный кэш от {updatedAt ?? "неизвестной даты"}"
                : _catalog.IsUsingCachedManifest
                    ? $"Локальный кэш версий · обновлён {updatedAt ?? "неизвестная дата"}"
                    : $"Манифест Mojang обновлён {updatedAt ?? "сейчас"}";
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
                    Families.Add(new MinecraftReleaseFamily(key, details.Title, details.Accent, details.Subtitle, options, ResolveArtUri(details.Art)));
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
            StatusText = Families.Count == 0 ? "Не удалось найти доступные релизы Minecraft." : "Список версий обновлён из манифеста Minecraft.";
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось загрузить манифест Minecraft: {ex.Message}";
            VersionCatalogStatus = "Не удалось получить манифест; корректного локального кэша нет.";
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
            StatusText = $"Подготавливаю Minecraft {SelectedVersion}…";
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
                StatusText = value.Message;
                LaunchProgress = value.Fraction * 100;
            });
            Process process = await _launchOrchestrator.LaunchAsync(
                instance, session, playerName, progress, launchCancellation.Token);
            LaunchProgress = 100;
            StatusText = $"Minecraft {SelectedVersion} запущен (PID {process.Id}).";
            _ = RecordPlayTimeAsync(process, instance);
        }
        catch (OperationCanceledException) { StatusText = "Запуск отменён."; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось запустить Minecraft {Version} с загрузчиком {Loader}", SelectedVersion, SelectedLoader);
            StatusText = $"Не удалось запустить игру: {DescribeFailure(ex)}";
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
        catch (Exception ex) { StatusText = $"Не удалось сохранить выбор версии: {ex.Message}"; }
    }

    private async Task LoadLoaderOptionsAsync(string minecraftVersion)
    {
        LoaderCatalogStatus = $"Проверяю загрузчики для Minecraft {minecraftVersion}…";
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
            LoaderCatalogStatus = unavailableOptions.Length == 0
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
            LoaderCatalogStatus = $"Не удалось проверить загрузчики: {ex.Message}. Vanilla остаётся доступна.";
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
                StatusText = $"Игра закрылась, но не удалось сохранить время запуска: {ex.Message}");
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
