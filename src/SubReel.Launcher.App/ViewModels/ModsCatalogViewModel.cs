using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Abstractions.Settings;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Models;

namespace SubReel.Launcher.App.ViewModels;

public partial class ModsCatalogViewModel : ObservableObject
{
    private readonly IReadOnlyList<IModpackCatalog> _sources;
    private readonly IModpackInstaller _installer;
    private readonly ICurseForgeModpackInstaller _curseForgeInstaller;
    private readonly IModpackTextTranslator _translator;
    private readonly ISettingsService _settings;
    private readonly IGameVersionCatalog _versionCatalog;
    private readonly IGameLoaderCatalog _loaderCatalog;
    private readonly InstanceService _instanceService;
    private CancellationTokenSource? _debounceCancellation;
    private CancellationTokenSource? _requestCancellation;
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private CancellationTokenSource? _installCancellation;
    private int _offset;
    private const int PageSize = 20;

    public ObservableCollection<ModpackCardViewModel> Results { get; } = [];
    public ObservableCollection<ModpackVersionSummary> ProjectVersions { get; } = [];
    public ObservableCollection<GameVersionInfo> GameVersions { get; } = [];
    public ObservableCollection<GameLoaderOption> AvailableLoaders { get; } = [];
    public IReadOnlyList<IModpackCatalog> Sources => _sources;
    public string[] Loaders { get; } = ["Any", "Fabric", "Forge", "NeoForge", "Quilt"];

    [ObservableProperty] private IModpackCatalog? _selectedSource;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _minecraftVersion = "";
    [ObservableProperty] private string _selectedLoader = "Any";
    [ObservableProperty] private string _statusText = "Ищи сборки Modrinth по названию или фильтрам.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private ModpackSummary? _selectedProject;
    [ObservableProperty] private string? _translatedProjectTitle;
    [ObservableProperty] private string? _translatedProjectDescription;
    [ObservableProperty] private bool _isCreatingCustom;
    [ObservableProperty] private bool _isLoadingWizardOptions;
    [ObservableProperty] private int _wizardStep = 1;
    [ObservableProperty] private GameVersionInfo? _selectedGameVersion;
    [ObservableProperty] private GameLoaderOption? _selectedGameLoader;
    [ObservableProperty] private string _customInstanceName = "Моя сборка";
    [ObservableProperty] private string _customMemoryMb = "4096";
    public bool IsProjectDetailsVisible => SelectedProject is not null && !IsCreatingCustom;
    public bool IsCatalogVisible => SelectedProject is null && !IsCreatingCustom;
    public bool IsWizardStepOne => WizardStep == 1;
    public bool IsWizardStepTwo => WizardStep == 2;
    public bool IsWizardStepThree => WizardStep == 3;
    public bool IsWizardStepFour => WizardStep == 4;
    public string WizardStepLabel => $"Шаг {WizardStep} из 4";
    public bool CanGoBack => WizardStep > 1;
    public string DisplayProjectTitle => TranslatedProjectTitle ?? SelectedProject?.Title ?? string.Empty;
    public string DisplayProjectDescription => TranslatedProjectDescription ?? SelectedProject?.Description ?? string.Empty;

    partial void OnSelectedProjectChanged(ModpackSummary? value)
    {
        OnPropertyChanged(nameof(DisplayProjectTitle));
        OnPropertyChanged(nameof(DisplayProjectDescription));
        OnPropertyChanged(nameof(IsCatalogVisible));
        OnPropertyChanged(nameof(IsProjectDetailsVisible));
    }

    partial void OnIsCreatingCustomChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCatalogVisible));
        OnPropertyChanged(nameof(IsProjectDetailsVisible));
    }

    partial void OnWizardStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsWizardStepOne));
        OnPropertyChanged(nameof(IsWizardStepTwo));
        OnPropertyChanged(nameof(IsWizardStepThree));
        OnPropertyChanged(nameof(IsWizardStepFour));
        OnPropertyChanged(nameof(WizardStepLabel));
        OnPropertyChanged(nameof(CanGoBack));
    }

    partial void OnSelectedGameVersionChanged(GameVersionInfo? value)
    {
        SelectedGameLoader = null;
        if (value is not null) _ = LoadWizardLoadersAsync(value.Id);
    }

    public ModsCatalogViewModel(IEnumerable<IModpackCatalog> sources, IModpackInstaller installer, IModpackTextTranslator translator,
        ICurseForgeModpackInstaller curseForgeInstaller, ISettingsService settings,
        IGameVersionCatalog versionCatalog, IGameLoaderCatalog loaderCatalog, InstanceService instanceService)
    {
        _sources = sources.ToArray();
        _installer = installer;
        _curseForgeInstaller = curseForgeInstaller;
        _translator = translator;
        _settings = settings;
        _versionCatalog = versionCatalog;
        _loaderCatalog = loaderCatalog;
        _instanceService = instanceService;
        _selectedSource = _sources.FirstOrDefault();
        if (SelectedSource is not null) _ = SearchAsync(reset: true);
    }

    [RelayCommand]
    private async Task BeginCustomCreationAsync()
    {
        IsCreatingCustom = true;
        SelectedProject = null;
        WizardStep = 1;
        if (GameVersions.Count > 0) return;
        try
        {
            IsLoadingWizardOptions = true;
            StatusText = "Загружаю версии Minecraft…";
            var versions = await _versionCatalog.GetVersionsAsync();
            foreach (var version in versions.Where(v => v.Type.Equals("release", StringComparison.OrdinalIgnoreCase)))
                GameVersions.Add(version);
            SelectedGameVersion = GameVersions.FirstOrDefault();
            StatusText = $"Доступно версий Minecraft: {GameVersions.Count}.";
        }
        catch (Exception ex) { StatusText = $"Не удалось загрузить версии: {ex.Message}"; }
        finally { IsLoadingWizardOptions = false; }
    }

    [RelayCommand]
    private void CancelCustomCreation() => IsCreatingCustom = false;

    [RelayCommand]
    private void ShowCatalog()
    {
        IsCreatingCustom = false;
        SelectedProject = null;
        ProjectVersions.Clear();
    }

    [RelayCommand]
    private void NextWizardStep()
    {
        if (WizardStep == 1 && SelectedGameVersion is null) { StatusText = "Выбери версию Minecraft."; return; }
        if (WizardStep == 2 && SelectedGameLoader is null) { StatusText = "Выбери доступный загрузчик."; return; }
        if (WizardStep == 3)
        {
            if (string.IsNullOrWhiteSpace(CustomInstanceName)) { StatusText = "Введи название сборки."; return; }
            if (!int.TryParse(CustomMemoryMb, out var memory) || memory is < 512 or > 65536)
            { StatusText = "Память должна быть числом от 512 до 65536 МБ."; return; }
        }
        if (WizardStep < 4) WizardStep++;
    }

    [RelayCommand]
    private void PreviousWizardStep()
    {
        if (WizardStep > 1) WizardStep--;
    }

    [RelayCommand]
    private async Task CreateCustomInstanceAsync()
    {
        if (IsBusy || SelectedGameVersion is null || SelectedGameLoader is null) return;
        if (!SelectedGameLoader.IsAvailable || !Enum.TryParse<MinecraftLoaderType>(SelectedGameLoader.Id, true, out var loader))
        { StatusText = "Выбранный загрузчик сейчас недоступен для этой версии."; return; }
        if (!int.TryParse(CustomMemoryMb, out var memory)) { StatusText = "Укажи объём памяти в МБ."; return; }
        try
        {
            IsBusy = true;
            StatusText = "Создаю игровой профиль…";
            var instance = await _instanceService.CreateAsync(CustomInstanceName, SelectedGameVersion.Id, loader,
                loader == MinecraftLoaderType.Vanilla ? null : SelectedGameLoader.Version, memory);
            IsCreatingCustom = false;
            StatusText = $"Сборка «{instance.Name}» создана. Она доступна во вкладке «Мои сборки».";
        }
        catch (Exception ex) { StatusText = $"Не удалось создать сборку: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task LoadWizardLoadersAsync(string minecraftVersion)
    {
        try
        {
            IsLoadingWizardOptions = true;
            AvailableLoaders.Clear();
            var loaders = await _loaderCatalog.GetLoadersAsync(minecraftVersion);
            foreach (var loader in loaders.Where(x => x.IsAvailable)) AvailableLoaders.Add(loader);
            SelectedGameLoader = AvailableLoaders.FirstOrDefault(x => x.Id == "Vanilla")
                ?? AvailableLoaders.FirstOrDefault(x => x.IsAvailable);
        }
        catch (Exception ex) { StatusText = $"Не удалось загрузить загрузчики: {ex.Message}"; }
        finally { IsLoadingWizardOptions = false; }
    }

    partial void OnSearchTextChanged(string value) => QueueSearch();
    partial void OnMinecraftVersionChanged(string value) => QueueSearch();
    partial void OnSelectedLoaderChanged(string value) => QueueSearch();
    partial void OnSelectedSourceChanged(IModpackCatalog? value) => QueueSearch();

    [RelayCommand]
    private async Task SearchAsync(bool reset = true)
    {
        var source = SelectedSource;
        if (source is null) return;
        var cancellation = new CancellationTokenSource();
        var previous = _requestCancellation;
        _requestCancellation = cancellation;
        previous?.Cancel();
        try { await _searchGate.WaitAsync(cancellation.Token); }
        catch (OperationCanceledException) { cancellation.Dispose(); return; }
        if (reset)
        {
            _offset = 0;
            Results.Clear();
        }

        try
        {
            IsBusy = true;
            StatusText = "Ищу сборки…";
            var page = await source.SearchAsync(new ModpackQuery(
                SearchText, string.IsNullOrWhiteSpace(MinecraftVersion) ? null : MinecraftVersion,
                SelectedLoader, _offset, PageSize), cancellation.Token);
            var cards = page.Items.Select(item => new ModpackCardViewModel(item)).ToArray();
            var launcherSettings = await _settings.LoadAsync(cancellation.Token);
            var targetLanguage = launcherSettings.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "RU" : "EN-US";
            string? translationWarning = null;
            if (targetLanguage == "RU")
            {
                try
                {
                    var sourceTexts = cards.SelectMany(card => new[] { card.Project.Title, card.Project.Description }).ToArray();
                    var translations = await _translator.TranslateAsync(sourceTexts, targetLanguage, cancellation.Token);
                    for (var i = 0; i < cards.Length; i++)
                    {
                        if (translations.Count > i * 2) cards[i].TranslatedTitle = translations[i * 2];
                        if (translations.Count > i * 2 + 1) cards[i].TranslatedDescription = translations[i * 2 + 1];
                    }
                }
                catch (InvalidOperationException ex) { translationWarning = ex.Message; }
                catch (Exception ex) when (ex is not OperationCanceledException) { translationWarning = $"Автоперевод недоступен: {ex.Message}"; }
            }
            foreach (var item in cards) Results.Add(item);
            _offset += page.Items.Count;
            HasMore = _offset < page.TotalHits;
            StatusText = page.TotalHits == 0 ? "По этому запросу сборок не найдено." : $"Найдено: {page.TotalHits:N0}. Показано: {Results.Count:N0}.";
            if (!string.IsNullOrWhiteSpace(translationWarning)) StatusText += $" {translationWarning}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusText = $"Не удалось загрузить каталог: {exception.Message}";
        }
        finally
        {
            _searchGate.Release();
            if (ReferenceEquals(_requestCancellation, cancellation))
            {
                _requestCancellation = null;
                IsBusy = false;
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private Task LoadMoreAsync() => SearchAsync(reset: false);

    [RelayCommand]
    private async Task ShowVersionsAsync(ModpackSummary? project)
    {
        if (project is null || SelectedSource is null) return;
        SelectedProject = project;
        TranslatedProjectTitle = null;
        TranslatedProjectDescription = null;
        ProjectVersions.Clear();
        try
        {
            IsBusy = true;
            StatusText = $"Загружаю версии «{project.Title}»…";
            var versions = await SelectedSource.GetVersionsAsync(project.Id);
            var orderedVersions = versions.Select((version, index) => (Version: version, Index: index))
                .OrderByDescending(item => item.Version.PublishedAt.HasValue)
                .ThenByDescending(item => item.Version.PublishedAt)
                .ThenBy(item => item.Index)
                .Select(item => item.Version)
                .ToArray();
            var instances = await _instanceService.GetAllAsync();
            var projectInstances = instances
                .Select(instance => (Instance: instance, Identity: GetInstalledModpackIdentity(instance)))
                .Where(item => item.Identity is not null &&
                    item.Identity.Value.Provider == project.Provider &&
                    string.Equals(item.Identity.Value.ProjectId, project.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            for (var versionIndex = 0; versionIndex < orderedVersions.Length; versionIndex++)
            {
                var version = orderedVersions[versionIndex];
                var installedInstance = projectInstances.FirstOrDefault(item =>
                    string.Equals(item.Identity!.Value.VersionId, version.Id, StringComparison.OrdinalIgnoreCase)).Instance;
                var otherInstalledInstance = projectInstances.FirstOrDefault(item =>
                    !string.Equals(item.Identity!.Value.VersionId, version.Id, StringComparison.OrdinalIgnoreCase)).Instance;
                ProjectVersions.Add(version with
                {
                    IsInstalled = installedInstance is not null,
                    IsUpdateAvailable = versionIndex == 0 && installedInstance is null && otherInstalledInstance is not null,
                    InstalledInstanceName = installedInstance?.Name ?? otherInstalledInstance?.Name
                });
            }
            StatusText = versions.Count == 0
                ? "У проекта пока нет опубликованных версий сборки."
                : projectInstances.Length > 0
                    ? $"Версий сборки: {versions.Count}. Обновление установится отдельным инстансом; текущая сборка останется без изменений."
                    : $"Версий сборки: {versions.Count}. Уже установленные версии отмечены в списке.";
        }
        catch (Exception exception)
        {
            StatusText = $"Не удалось загрузить версии сборки: {exception.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void CloseVersions()
    {
        SelectedProject = null;
        ProjectVersions.Clear();
    }

    [RelayCommand]
    private void OpenProjectPage(ModpackCardViewModel? card)
    {
        if (card is null) return;
        TranslatedProjectTitle = card.TranslatedTitle;
        TranslatedProjectDescription = card.TranslatedDescription;
        _ = ShowVersionsAsync(card.Project);
    }

    [RelayCommand]
    private void OpenVersion(ModpackVersionSummary? version)
    {
        if (SelectedProject is null || version is null) return;
        var url = SelectedProject.Provider == ModpackProvider.CurseForge
            ? $"https://www.curseforge.com/minecraft/modpacks/{Uri.EscapeDataString(SelectedProject.Slug)}/files/{Uri.EscapeDataString(version.Id)}"
            : $"https://modrinth.com/modpack/{Uri.EscapeDataString(SelectedProject.Slug)}/version/{Uri.EscapeDataString(version.Id)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenProjectSource(ModpackSummary? project)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.Slug)) return;
        var url = project.Provider == ModpackProvider.CurseForge
            ? $"https://www.curseforge.com/minecraft/modpacks/{Uri.EscapeDataString(project.Slug)}"
            : $"https://modrinth.com/modpack/{Uri.EscapeDataString(project.Slug)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task InstallVersionAsync(ModpackVersionSummary? version)
    {
        if (version is null || SelectedProject is null || IsBusy || version.IsInstalled) return;
        var project = SelectedProject;
        try
        {
            IsBusy = true;
            IsInstalling = true;
            using var cancellation = new CancellationTokenSource();
            _installCancellation = cancellation;
            var progress = new Progress<ModpackInstallProgress>(update =>
            {
                StatusText = update.Message;
            });
            var installed = project.Provider == ModpackProvider.CurseForge
                ? await _curseForgeInstaller.InstallAsync(project, version, progress: progress, cancellationToken: cancellation.Token)
                : await _installer.InstallAsync(project, version, progress: progress, cancellationToken: cancellation.Token);
            StatusText = $"Сборка «{installed.Name}» добавлена в «Мои сборки» ({installed.Loader}, Minecraft {installed.MinecraftVersion}). Первый запуск подготовит игровые файлы." +
                (installed.Warning is null ? "" : $" {installed.Warning}");
            var installedVersionIndex = ProjectVersions.IndexOf(version);
            if (installedVersionIndex >= 0)
                ProjectVersions[installedVersionIndex] = version with
                {
                    IsInstalled = true,
                    InstalledInstanceName = installed.Name
                };
        }
        catch (OperationCanceledException) { StatusText = "Установка сборки отменена."; }
        catch (Exception exception)
        {
            StatusText = $"Не удалось установить модпак: {exception.Message}";
        }
        finally { _installCancellation = null; IsInstalling = false; IsBusy = false; }
    }

    [RelayCommand]
    private void CancelInstall() => _installCancellation?.Cancel();

    private static (ModpackProvider Provider, string ProjectId, string VersionId)? GetInstalledModpackIdentity(MinecraftInstance instance)
    {
        var manifest = instance.ModpackManifest;
        if (manifest is not null && Enum.TryParse<ModpackProvider>(manifest.Provider, true, out var provider) && Enum.IsDefined(provider) &&
            !string.IsNullOrWhiteSpace(manifest.ProjectId) && !string.IsNullOrWhiteSpace(manifest.VersionId))
            return (provider, manifest.ProjectId, manifest.VersionId);

        var parts = instance.Source?.Split(':', 3, StringSplitOptions.TrimEntries);
        return parts is { Length: 3 } && Enum.TryParse<ModpackProvider>(parts[0], true, out provider) && Enum.IsDefined(provider) &&
                        !string.IsNullOrWhiteSpace(parts[1]) && !string.IsNullOrWhiteSpace(parts[2])
            ? (provider, parts[1], parts[2])
            : null;
    }

    private async void QueueSearch()
    {
        _debounceCancellation?.Cancel();
        _debounceCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _debounceCancellation = cancellation;
        _requestCancellation?.Cancel();
        try
        {
            await Task.Delay(300, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                await SearchAsync(reset: true);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_debounceCancellation, cancellation)) _debounceCancellation = null;
            cancellation.Dispose();
        }
    }
}
