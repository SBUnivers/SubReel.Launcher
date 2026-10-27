using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Abstractions.Settings;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Models;
using SubReel.Launcher.App.Services;

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
    private readonly ILocalizationService _localization;
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
    [ObservableProperty] private string _statusTextRaw = "Ищи сборки Modrinth по названию или фильтрам.";
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
    public bool IsCatalogEmpty => IsCatalogVisible && Results.Count == 0 && !IsBusy;
    public bool IsWizardStepOne => WizardStep == 1;
    public bool IsWizardStepTwo => WizardStep == 2;
    public bool IsWizardStepThree => WizardStep == 3;
    public bool IsWizardStepFour => WizardStep == 4;
    public string WizardStepLabel => string.Format(_localization.Culture,
        _localization.Get("Mods.WizardStep", "Шаг {0} из 4"), WizardStep);
    public string WizardMinecraftSummary => string.Format(_localization.Culture,
        _localization.Get("Mods.SummaryMinecraft", "Minecraft {0}"), SelectedGameVersion?.Id ?? "—");
    public string WizardLoaderSummary => string.Format(_localization.Culture,
        _localization.Get("Mods.SummaryLoader", "Загрузчик: {0}"), SelectedGameLoader?.Name ?? "—");
    public string WizardMemorySummary => string.Format(_localization.Culture,
        _localization.Get("Mods.SummaryMemory", "Память: {0} МБ"), CustomMemoryMb);
    public string StatusText => TranslateStatus(StatusTextRaw);
    partial void OnStatusTextRawChanged(string value) => OnPropertyChanged(nameof(StatusText));
    private static string StatusResourceKey(string text) => text switch
    {
        "Ищи сборки Modrinth по названию или фильтрам." => "Mods.Status.SearchHint",
        "Загружаю версии Minecraft…" => "Mods.Status.LoadingVersions",
        "Выбери версию Minecraft." => "Mods.Status.ChooseVersion",
        "Выбери доступный загрузчик." => "Mods.Status.ChooseLoader",
        "Введи название сборки." => "Mods.Status.EnterName",
        "Память должна быть числом от 512 до 65536 МБ." => "Mods.Status.InvalidMemoryRange",
        "Выбранный загрузчик сейчас недоступен для этой версии." => "Mods.Status.LoaderUnavailable",
        "Укажи объём памяти в МБ." => "Mods.Status.EnterMemory",
        "Создаю игровой профиль…" => "Mods.Status.CreatingProfile",
        "Ищу сборки…" => "Mods.Status.Searching",
        "Установка сборки отменена." => "Mods.Status.InstallCancelled",
        "Загружаю версии сборки…" => "Mods.Status.LoadingProjectVersions",
        "Читаю манифест модпака…" => "Mods.Status.ReadingManifest",
        _ => "Mods.Status.Unknown"
    };

    private string TranslateStatus(string text)
    {
        var translationMarker = " Автоперевод недоступен: ";
        var translationWarningIndex = text.IndexOf(translationMarker, StringComparison.Ordinal);
        if (translationWarningIndex >= 0)
        {
            var mainStatus = TranslateStatus(text[..translationWarningIndex]);
            var warning = FormatStatus("Mods.Status.TranslationUnavailable", translationMarker.TrimStart() + "{0}",
                text[(translationWarningIndex + translationMarker.Length)..]);
            return $"{mainStatus} {warning}";
        }
        var key = StatusResourceKey(text);
        if (key != "Mods.Status.Unknown") return _localization.Get(key, text);
        if (text == "По этому запросу сборок не найдено.")
            return _localization.Get("Mods.Status.NoResults", text);
        if (text == "Скачиваю архив Modrinth…")
            return _localization.Get("Mods.Status.DownloadingArchive", text);
        if (text == "Сборка установлена. Minecraft подготовится при первом запуске.")
            return _localization.Get("Mods.Status.ArchiveInstalled", text);
        foreach (var (prefix, resourceKey) in new[]
        {
            ("Загружено файлов: ", "Mods.Status.DownloadedFiles"),
            ("Распаковка файлов: ", "Mods.Status.ExtractingFiles")
        })
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var separator = text.IndexOf('/', prefix.Length);
            if (separator > prefix.Length)
                return FormatStatus(resourceKey, text, text[prefix.Length..separator], text[(separator + 1)..]);
        }
        if (text.StartsWith("Сборка «", StringComparison.Ordinal) && text.EndsWith("» создана. Она доступна во вкладке «Мои сборки».", StringComparison.Ordinal))
            return FormatStatus("Mods.Status.InstanceCreated", text, text[8..^"» создана. Она доступна во вкладке «Мои сборки».".Length]);
        if (text.StartsWith("Сборка «", StringComparison.Ordinal))
        {
            const string installedMarker = "» добавлена в «Мои сборки» (";
            const string installedSuffix = "). Первый запуск подготовит игровые файлы.";
            var nameEnd = text.IndexOf(installedMarker, 8, StringComparison.Ordinal);
            if (nameEnd > 8)
            {
                var loaderSeparator = text.IndexOf(", Minecraft ", nameEnd + installedMarker.Length, StringComparison.Ordinal);
                if (loaderSeparator > nameEnd)
                {
                    var versionStart = loaderSeparator + ", Minecraft ".Length;
                    var versionEnd = text.IndexOf(installedSuffix, versionStart, StringComparison.Ordinal);
                    if (versionEnd >= versionStart)
                    {
                        var warning = text[(versionEnd + installedSuffix.Length)..].Trim();
                        var message = FormatStatus("Mods.Status.InstanceInstalled", text,
                            text[8..nameEnd],
                            text[(nameEnd + installedMarker.Length)..loaderSeparator],
                            text[versionStart..versionEnd]);
                        return string.IsNullOrWhiteSpace(warning) ? message : $"{message} {warning}";
                    }
                }
            }
        }
        if (text.StartsWith("Найдено: ", StringComparison.Ordinal) && text.Contains(". Показано: ", StringComparison.Ordinal) && text.EndsWith('.'))
        {
            var shownSeparator = text.IndexOf(". Показано: ", StringComparison.Ordinal);
            return FormatStatus("Mods.Status.SearchResults", text, text[9..shownSeparator], text[(shownSeparator + ". Показано: ".Length)..^1]);
        }
        if (text == "У проекта пока нет опубликованных версий сборки.")
            return _localization.Get("Mods.Status.NoProjectVersions", text);
        if (text.StartsWith("Версий сборки: ", StringComparison.Ordinal))
        {
            var countEnd = text.IndexOf('.', StringComparison.Ordinal);
            if (countEnd > 15)
            {
                var hasOtherInstance = text.Contains("Уже установленные версии отмечены", StringComparison.Ordinal);
                return FormatStatus(hasOtherInstance ? "Mods.Status.ProjectVersionsAvailable" : "Mods.Status.ProjectVersionsUpdate",
                    text, text[15..countEnd]);
            }
        }
        if (text.StartsWith("Автоперевод недоступен: ", StringComparison.Ordinal))
            return FormatStatus("Mods.Status.TranslationUnavailable", text, text["Автоперевод недоступен: ".Length..]);
        if (text.StartsWith("Доступно версий Minecraft: ", StringComparison.Ordinal) && text.EndsWith(".", StringComparison.Ordinal) &&
            int.TryParse(text[27..^1], out var versionCount))
            return FormatStatus("Mods.Status.AvailableVersions", text, versionCount);
        if (text.StartsWith("Загружаю версии «", StringComparison.Ordinal) && text.EndsWith("»…", StringComparison.Ordinal))
            return FormatStatus("Mods.Status.LoadingProject", text, text[17..^2]);

        var prefixes = new (string Prefix, string Key)[]
        {
            ("Не удалось загрузить версии: ", "Mods.Status.VersionsFailed"),
            ("Не удалось создать сборку: ", "Mods.Status.CreateFailed"),
            ("Не удалось загрузить загрузчики: ", "Mods.Status.LoadersFailed"),
            ("Не удалось загрузить каталог: ", "Mods.Status.CatalogFailed"),
            ("Не удалось загрузить версии сборки: ", "Mods.Status.ProjectVersionsFailed"),
            ("Не удалось установить модпак: ", "Mods.Status.InstallFailed")
        };
        foreach (var (prefix, resourceKey) in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                return FormatStatus(resourceKey, prefix + "{0}", text[prefix.Length..]);
        }
        return text;
    }

    private string FormatStatus(string key, string fallback, params object[] args) =>
        string.Format(_localization.Culture, _localization.Get(key, fallback), args);
    public bool CanGoBack => WizardStep > 1;
    public string DisplayProjectTitle => TranslatedProjectTitle ?? SelectedProject?.Title ?? string.Empty;
    public string DisplayProjectDescription => TranslatedProjectDescription ?? SelectedProject?.Description ?? string.Empty;
    public string? SelectedProjectAuthorLabel => string.IsNullOrWhiteSpace(SelectedProject?.Author)
        ? null
        : string.Format(_localization.Culture, _localization.Get("Mods.AuthorLabel", "Author: {0}"), SelectedProject.Author);
    public string SelectedDownloadsLabel => SelectedProject is null
        ? string.Empty
        : string.Format(_localization.Culture, _localization.Get("Mods.DownloadsLabel", "{0} downloads"),
            SelectedProject.Downloads.ToString("N0", _localization.Culture));

    partial void OnSelectedProjectChanged(ModpackSummary? value)
    {
        OnPropertyChanged(nameof(DisplayProjectTitle));
        OnPropertyChanged(nameof(DisplayProjectDescription));
        OnPropertyChanged(nameof(IsCatalogVisible));
        OnPropertyChanged(nameof(IsProjectDetailsVisible));
        OnPropertyChanged(nameof(IsCatalogEmpty));
    }

    partial void OnSelectedGameVersionChanged(GameVersionInfo? value)
    {
        SelectedGameLoader = null;
        OnPropertyChanged(nameof(WizardMinecraftSummary));
        if (value is not null) _ = LoadWizardLoadersAsync(value.Id);
    }

    partial void OnSelectedGameLoaderChanged(GameLoaderOption? value) => OnPropertyChanged(nameof(WizardLoaderSummary));
    partial void OnCustomMemoryMbChanged(string value) => OnPropertyChanged(nameof(WizardMemorySummary));

    partial void OnIsCreatingCustomChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCatalogVisible));
        OnPropertyChanged(nameof(IsProjectDetailsVisible));
        OnPropertyChanged(nameof(IsCatalogEmpty));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsCatalogEmpty));

    partial void OnWizardStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsWizardStepOne));
        OnPropertyChanged(nameof(IsWizardStepTwo));
        OnPropertyChanged(nameof(IsWizardStepThree));
        OnPropertyChanged(nameof(IsWizardStepFour));
        OnPropertyChanged(nameof(WizardStepLabel));
        OnPropertyChanged(nameof(CanGoBack));
    }

    public ModsCatalogViewModel(IEnumerable<IModpackCatalog> sources, IModpackInstaller installer, IModpackTextTranslator translator,
        ICurseForgeModpackInstaller curseForgeInstaller, ISettingsService settings,
        IGameVersionCatalog versionCatalog, IGameLoaderCatalog loaderCatalog, InstanceService instanceService, ILocalizationService localization)
    {
        _sources = sources.ToArray();
        _installer = installer;
        _curseForgeInstaller = curseForgeInstaller;
        _translator = translator;
        _settings = settings;
        _versionCatalog = versionCatalog;
        _loaderCatalog = loaderCatalog;
        _instanceService = instanceService;
        _localization = localization;
        _customInstanceName = _localization.Get("Mods.NewInstanceDefault", "Моя сборка");
        _selectedSource = _sources.FirstOrDefault();
        Results.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsCatalogEmpty));
        _localization.LanguageChanged += (_, _) =>
        {
            if (_customInstanceName is "Моя сборка" or "My modpack")
                CustomInstanceName = _localization.Get("Mods.NewInstanceDefault", "Моя сборка");
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(WizardStepLabel));
            OnPropertyChanged(nameof(WizardMinecraftSummary));
            OnPropertyChanged(nameof(WizardLoaderSummary));
            OnPropertyChanged(nameof(WizardMemorySummary));
            OnPropertyChanged(nameof(SelectedProjectAuthorLabel));
            OnPropertyChanged(nameof(SelectedDownloadsLabel));
            foreach (var card in Results) card.RefreshLocalization();
        };
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
            StatusTextRaw = "Загружаю версии Minecraft…";
            var versions = await _versionCatalog.GetVersionsAsync();
            foreach (var version in versions.Where(v => v.Type.Equals("release", StringComparison.OrdinalIgnoreCase)))
                GameVersions.Add(version);
            SelectedGameVersion = GameVersions.FirstOrDefault();
            StatusTextRaw = $"Доступно версий Minecraft: {GameVersions.Count}.";
        }
        catch (Exception ex) { StatusTextRaw = $"Не удалось загрузить версии: {ex.Message}"; }
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
        if (WizardStep == 1 && SelectedGameVersion is null) { StatusTextRaw = "Выбери версию Minecraft."; return; }
        if (WizardStep == 2 && SelectedGameLoader is null) { StatusTextRaw = "Выбери доступный загрузчик."; return; }
        if (WizardStep == 3)
        {
            if (string.IsNullOrWhiteSpace(CustomInstanceName)) { StatusTextRaw = "Введи название сборки."; return; }
            if (!int.TryParse(CustomMemoryMb, out var memory) || memory is < 512 or > 65536)
            { StatusTextRaw = "Память должна быть числом от 512 до 65536 МБ."; return; }
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
        { StatusTextRaw = "Выбранный загрузчик сейчас недоступен для этой версии."; return; }
        if (!int.TryParse(CustomMemoryMb, out var memory)) { StatusTextRaw = "Укажи объём памяти в МБ."; return; }
        try
        {
            IsBusy = true;
            StatusTextRaw = "Создаю игровой профиль…";
            var instance = await _instanceService.CreateAsync(CustomInstanceName, SelectedGameVersion.Id, loader,
                loader == MinecraftLoaderType.Vanilla ? null : SelectedGameLoader.Version, memory);
            IsCreatingCustom = false;
            StatusTextRaw = $"Сборка «{instance.Name}» создана. Она доступна во вкладке «Мои сборки».";
        }
        catch (Exception ex) { StatusTextRaw = $"Не удалось создать сборку: {ex.Message}"; }
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
        catch (Exception ex) { StatusTextRaw = $"Не удалось загрузить загрузчики: {ex.Message}"; }
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
            StatusTextRaw = "Ищу сборки…";
            var page = await source.SearchAsync(new ModpackQuery(
                SearchText, string.IsNullOrWhiteSpace(MinecraftVersion) ? null : MinecraftVersion,
                SelectedLoader, _offset, PageSize), cancellation.Token);
            var cards = page.Items.Select(item => new ModpackCardViewModel(item, _localization)).ToArray();
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
            StatusTextRaw = page.TotalHits == 0 ? "По этому запросу сборок не найдено."
                : $"Найдено: {page.TotalHits.ToString("N0", _localization.Culture)}. Показано: {Results.Count.ToString("N0", _localization.Culture)}.";
            if (!string.IsNullOrWhiteSpace(translationWarning))
                StatusTextRaw += $" Автоперевод недоступен: {translationWarning}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusTextRaw = $"Не удалось загрузить каталог: {exception.Message}";
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
            StatusTextRaw = $"Загружаю версии «{project.Title}»…";
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
            StatusTextRaw = versions.Count == 0
                ? "У проекта пока нет опубликованных версий сборки."
                : projectInstances.Length > 0
                    ? $"Версий сборки: {versions.Count}. Обновление установится отдельным инстансом; текущая сборка останется без изменений."
                    : $"Версий сборки: {versions.Count}. Уже установленные версии отмечены в списке.";
        }
        catch (Exception exception)
        {
            StatusTextRaw = $"Не удалось загрузить версии сборки: {exception.Message}";
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
                StatusTextRaw = update.Message;
            });
            var installed = project.Provider == ModpackProvider.CurseForge
                ? await _curseForgeInstaller.InstallAsync(project, version, progress: progress, cancellationToken: cancellation.Token)
                : await _installer.InstallAsync(project, version, progress: progress, cancellationToken: cancellation.Token);
            StatusTextRaw = $"Сборка «{installed.Name}» добавлена в «Мои сборки» ({installed.Loader}, Minecraft {installed.MinecraftVersion}). Первый запуск подготовит игровые файлы." +
                (installed.Warning is null ? "" : $" {installed.Warning}");
            var installedVersionIndex = ProjectVersions.IndexOf(version);
            if (installedVersionIndex >= 0)
                ProjectVersions[installedVersionIndex] = version with
                {
                    IsInstalled = true,
                    InstalledInstanceName = installed.Name
                };
        }
        catch (OperationCanceledException) { StatusTextRaw = "Установка сборки отменена."; }
        catch (Exception exception)
        {
            StatusTextRaw = $"Не удалось установить модпак: {exception.Message}";
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
