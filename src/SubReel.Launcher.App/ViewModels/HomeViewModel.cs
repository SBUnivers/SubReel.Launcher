using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.App.DesignSystem;

namespace SubReel.Launcher.App.ViewModels;

public sealed record MinecraftVersionOption(string Id, bool IsSnapshot, bool IsLatest)
{
    public string Label => IsSnapshot ? $"{Id}  ·  snapshot" : Id;
}

public sealed record MinecraftReleaseFamily(string Id, string Name, string Accent, string Tagline, IReadOnlyList<MinecraftVersionOption> Versions)
{
    public string LatestVersion => Versions.FirstOrDefault(x => !x.IsSnapshot)?.Id ?? Versions.FirstOrDefault()?.Id ?? Id;
    public string LatestVersionLabel => $"Последняя версия {LatestVersion}";
}

public partial class HomeViewModel : ObservableObject
{
    private static readonly string[] LoaderOptions = ["Vanilla", "Fabric", "NeoForge", "Forge"];
    private static readonly Dictionary<string, (string Name, string Accent, string Tagline)> ReleaseDetails = new()
    {
        ["1.21"] = ("Tricky Trials", "#A78BFA", "ИСПЫТАНИЯ ЖДУТ"),
        ["1.20"] = ("Trails & Tales", "#F7B955", "ИСТОРИИ И ПУТЕШЕСТВИЯ"),
        ["1.19"] = ("The Wild Update", "#78C897", "ДИКАЯ ПРИРОДА"),
        ["1.18"] = ("Caves & Cliffs", "#6FC8EF", "МИР СТАЛ ГЛУБЖЕ")
    };

    private readonly IGameVersionCatalog _catalog;
    private readonly IGameService _game;
    private readonly IGameAuthService _auth;
    private readonly IThemeService _theme;
    private readonly string _gameDirectory;
    private CancellationTokenSource? _launchCancellation;

    public ObservableCollection<MinecraftReleaseFamily> Families { get; } = [];
    public ObservableCollection<MinecraftVersionOption> ExactVersions { get; } = [];
    public IReadOnlyList<string> Loaders => LoaderOptions;

    [ObservableProperty] private MinecraftReleaseFamily? _selectedFamily;
    [ObservableProperty] private string _selectedVersion = "";
    [ObservableProperty] private string _selectedLoader = "Vanilla";
    [ObservableProperty] private string _statusText = "Загружаю официальный список версий Minecraft…";
    [ObservableProperty] private string _selectedAccent = "#8875FF";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private double _launchProgress;
    public string SelectedVersionSummary => string.IsNullOrEmpty(SelectedVersion) ? "Выбери версию слева." : $"Будет подготовлена версия {SelectedVersion}";

    public HomeViewModel(IGameVersionCatalog catalog, IGameService game, IGameAuthService auth, IAppPaths paths, IThemeService theme)
    {
        _catalog = catalog;
        _game = game;
        _auth = auth;
        _theme = theme;
        _gameDirectory = Path.Combine(paths.Instances, "default", "game");
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
    }

    partial void OnSelectedVersionChanged(string value) => OnPropertyChanged(nameof(SelectedVersionSummary));

    [RelayCommand]
    private async Task LoadVersionsAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            var versions = await _catalog.GetVersionsAsync();
            var releaseVersions = versions.Where(x => x.Type.Equals("release", StringComparison.OrdinalIgnoreCase) || x.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase));
            foreach (var group in releaseVersions.GroupBy(x => FamilyKey(x.Id)).OrderByDescending(g => VersionSortKey(g.Key)).Take(8))
            {
                var key = group.Key;
                var details = ReleaseDetails.TryGetValue(key, out var known) ? known : (key, "#8875FF", "MINECRAFT UPDATE");
                var ordered = group.OrderByDescending(x => x.Type.Equals("release", StringComparison.OrdinalIgnoreCase)).Take(30).ToArray();
                var options = ordered.Select((x, index) => new MinecraftVersionOption(x.Id, x.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase), index == 0))
                    .ToArray();
                if (options.Length > 0) Families.Add(new MinecraftReleaseFamily(key, details.Item1, details.Item2, details.Item3, options));
            }
            SelectedFamily = Families.FirstOrDefault();
            StatusText = Families.Count == 0 ? "Не удалось найти доступные релизы Minecraft." : "Список версий обновлён из манифеста Minecraft.";
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось загрузить манифест Minecraft: {ex.Message}";
        }
        finally { IsBusy = false; }
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
            var session = await _auth.GetActiveSessionAsync(launchCancellation.Token);
            var playerName = _auth.ActiveAccount?.Username ?? session?.Username ?? "SubReelPlayer";
            var request = new GameLaunchRequest(
                _gameDirectory, SelectedVersion, 4096,
                playerName, session,
                SelectedLoader);
            var progress = new Progress<GameInstallProgress>(value =>
            {
                StatusText = value.Stage;
                LaunchProgress = value.Fraction * 100;
            });
            Process process = await _game.LaunchAsync(request, progress, launchCancellation.Token);
            LaunchProgress = 100;
            StatusText = $"Minecraft {SelectedVersion} запущен (PID {process.Id}).";
        }
        catch (OperationCanceledException) { StatusText = "Запуск отменён."; }
        catch (Exception ex) { StatusText = $"Не удалось запустить игру: {ex.Message}"; }
        finally { _launchCancellation = null; IsBusy = false; }
    }

    [RelayCommand]
    private void CancelLaunch() => _launchCancellation?.Cancel();

    private static string FamilyKey(string id)
    {
        var parts = id.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var first) && int.TryParse(parts[1].Split('-')[0], out _))
            return first >= 20 ? parts[0] : $"{parts[0]}.{parts[1]}";
        return id;
    }

    private static double VersionSortKey(string value)
    {
        var parts=value.Split('.');
        return double.TryParse(parts[0],out var first) ? first * 1000 + (parts.Length > 1 && double.TryParse(parts[1],out var second) ? second : 0) : 0;
    }
}
