
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Abstractions.Settings;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Launching;
using SubReel.Launcher.Core.Models;

namespace SubReel.Launcher.App.ViewModels;

public partial class InstancesViewModel : ObservableObject
{
    private readonly InstanceService _service;
    private readonly ILaunchOrchestrator _launchOrchestrator;
    private readonly IGameAuthService _authService;
    private readonly IGameLoaderCatalog _loaderCatalog;
    private readonly IJavaRuntimeInspector _javaRuntimeInspector;
    private readonly ISettingsService _settings;
    private readonly IGameVersionCatalog _versionCatalog;
    private readonly ILogger<InstancesViewModel> _logger;
    private readonly IReadOnlyList<IModpackCatalog> _modpackCatalogs;
    private readonly IModpackInstaller _modrinthInstaller;
    private readonly ICurseForgeModpackInstaller _curseForgeInstaller;

    public ObservableCollection<MinecraftInstance> Instances { get; } = [];
    public ICollectionView FilteredInstances { get; }
    public string[] SortOptions { get; } = ["Название", "Последний запуск", "Сначала избранное"];
    public bool HasInstances => Instances.Count > 0;
    public bool HasSearchNoResults => HasInstances && !FilteredInstances.Cast<MinecraftInstance>().Any();

    public MinecraftLoaderType[] LoaderTypes { get; } =
        [MinecraftLoaderType.Vanilla, MinecraftLoaderType.Fabric,
         MinecraftLoaderType.Forge, MinecraftLoaderType.NeoForge, MinecraftLoaderType.Quilt];

    [ObservableProperty]
    private MinecraftInstance? _selectedInstance;

    [ObservableProperty]
    private string _instanceName = "Моя сборка";

    [ObservableProperty]
    private string _minecraftVersion = "1.21.1";

    [ObservableProperty]
    private MinecraftLoaderType _selectedLoader = MinecraftLoaderType.Vanilla;

    [ObservableProperty]
    private string _loaderVersion = "";

    [ObservableProperty]
    private string _memoryMbInput = "4096";

    [ObservableProperty]
    private string _statusText = "Готово";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedSortOption = "Сначала избранное";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLaunching;

    private CancellationTokenSource? _launchCancellation;
    private MinecraftLoaderType? _initialSelectedLoaderType;
    private string? _initialSelectedMinecraftVersion;

    [ObservableProperty]
    private double _launchProgress;

    [ObservableProperty]
    private string _playerName = "SubReelPlayer";

    public InstancesViewModel(InstanceService service, ILaunchOrchestrator launchOrchestrator, IGameAuthService authService, IGameLoaderCatalog loaderCatalog, IJavaRuntimeInspector javaRuntimeInspector, ISettingsService settings, IGameVersionCatalog versionCatalog, ILogger<InstancesViewModel> logger, IEnumerable<IModpackCatalog> modpackCatalogs, IModpackInstaller modrinthInstaller, ICurseForgeModpackInstaller curseForgeInstaller)
    {
        _service = service;
        _launchOrchestrator = launchOrchestrator;
        _authService = authService;
        _loaderCatalog = loaderCatalog;
        _javaRuntimeInspector = javaRuntimeInspector;
        _settings = settings;
        _versionCatalog = versionCatalog;
        _logger = logger;
        _modpackCatalogs = modpackCatalogs.ToArray();
        _modrinthInstaller = modrinthInstaller;
        _curseForgeInstaller = curseForgeInstaller;
        FilteredInstances = CollectionViewSource.GetDefaultView(Instances);
        FilteredInstances.Filter = FilterInstance;
        Instances.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasInstances));
            OnPropertyChanged(nameof(HasSearchNoResults));
            OnPropertyChanged(nameof(EmptyStateText));
        };
        ApplyInstanceSort();
    }

    partial void OnSearchTextChanged(string value)
    {
        FilteredInstances.Refresh();
        OnPropertyChanged(nameof(HasSearchNoResults));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    partial void OnSelectedSortOptionChanged(string value) => ApplyInstanceSort();

    private bool FilterInstance(object item)
    {
        if (item is not MinecraftInstance instance || string.IsNullOrWhiteSpace(SearchText))
            return item is MinecraftInstance;

        var query = SearchText.Trim();
        return instance.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               instance.MinecraftVersion.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               instance.LoaderType.ToString().Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               (instance.LoaderVersion?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void ApplyInstanceSort()
    {
        if (FilteredInstances is null) return;
        using (FilteredInstances.DeferRefresh())
        {
            FilteredInstances.SortDescriptions.Clear();
            if (SelectedSortOption == "Последний запуск")
            {
                FilteredInstances.SortDescriptions.Add(new SortDescription(nameof(MinecraftInstance.LastPlayedAt), ListSortDirection.Descending));
                FilteredInstances.SortDescriptions.Add(new SortDescription(nameof(MinecraftInstance.Name), ListSortDirection.Ascending));
            }
            else if (SelectedSortOption == "Сначала избранное")
            {
                FilteredInstances.SortDescriptions.Add(new SortDescription(nameof(MinecraftInstance.IsFavorite), ListSortDirection.Descending));
                FilteredInstances.SortDescriptions.Add(new SortDescription(nameof(MinecraftInstance.Name), ListSortDirection.Ascending));
            }
            else
            {
                FilteredInstances.SortDescriptions.Add(new SortDescription(nameof(MinecraftInstance.Name), ListSortDirection.Ascending));
            }
        }
        OnPropertyChanged(nameof(HasSearchNoResults));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    public string EmptyStateText => HasInstances
        ? "По запросу ничего не найдено. Попробуй другое название, версию или загрузчик."
        : "Сборок пока нет. Создай первую сборку справа.";

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusText = "Загрузка сборок…";

            await RefreshItemsAsync();

            StatusText = $"Сборок: {Instances.Count}";
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось загрузить сборки: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsBusy)
            return;

        if (!int.TryParse(MemoryMbInput, out var memoryMb))
        {
            StatusText = "Введи объём памяти в МБ, например 4096.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Создание сборки…";

            var loaderVersion = string.IsNullOrWhiteSpace(LoaderVersion) ? null : LoaderVersion.Trim();
            if (SelectedLoader != MinecraftLoaderType.Vanilla)
            {
                var loaderOptions = await _loaderCatalog.GetLoadersAsync(MinecraftVersion);
                var selectedOption = loaderOptions.FirstOrDefault(option =>
                    option.Id.Equals(SelectedLoader.ToString(), StringComparison.OrdinalIgnoreCase));
                if (selectedOption is null || !selectedOption.IsAvailable)
                {
                    if (selectedOption?.IsCatalogError != true || string.IsNullOrWhiteSpace(loaderVersion))
                        throw new InvalidOperationException(selectedOption?.AvailabilityMessage ??
                            $"Не удалось проверить поддержку {SelectedLoader} для Minecraft {MinecraftVersion}.");
                }
                else if (string.IsNullOrWhiteSpace(loaderVersion))
                {
                    loaderVersion = selectedOption.Version;
                }

                if (string.IsNullOrWhiteSpace(loaderVersion))
                    throw new InvalidOperationException($"Не удалось определить версию {SelectedLoader}. Укажи её вручную и попробуй снова.");
            }

            var instance = await _service.CreateAsync(
                InstanceName,
                MinecraftVersion,
                SelectedLoader,
                loaderVersion,
                memoryMb);

            Instances.Add(instance);
            SelectedInstance = instance;

            StatusText = $"Сборка «{instance.Name}» сохранена.";
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось создать сборку: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveSelectedAsync()
    {
        if (IsBusy || SelectedInstance is null)
            return;

        try
        {
            IsBusy = true;

            var instance = SelectedInstance;
            var instanceName = instance.Name;
            var installTargetChanged = _initialSelectedLoaderType != instance.LoaderType ||
                !string.Equals(_initialSelectedMinecraftVersion, instance.MinecraftVersion, StringComparison.OrdinalIgnoreCase);
            if (instance.LoaderType == MinecraftLoaderType.Vanilla)
            {
                instance.LoaderVersion = null;
            }
            else if (installTargetChanged)
            {
                StatusText = $"Проверяю {instance.LoaderType} для Minecraft {instance.MinecraftVersion}…";
                var options = await _loaderCatalog.GetLoadersAsync(instance.MinecraftVersion);
                var option = options.FirstOrDefault(item =>
                    item.Id.Equals(instance.LoaderType.ToString(), StringComparison.OrdinalIgnoreCase));
                if (option is null || !option.IsAvailable)
                    throw new InvalidOperationException(option?.AvailabilityMessage ??
                        $"Не удалось проверить {instance.LoaderType} для Minecraft {instance.MinecraftVersion}.");
                if (string.IsNullOrWhiteSpace(option.Version))
                    throw new InvalidOperationException($"Каталог не вернул версию загрузчика {instance.LoaderType}.");

                instance.LoaderVersion = option.Version;
            }

            await _service.UpdateAsync(instance);
            await RefreshItemsAsync();
            SelectedInstance = Instances.FirstOrDefault(item => item.Id == instance.Id);

            StatusText = $"Сборка «{instanceName}» сохранена.";
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось сохранить изменения: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InspectJavaAsync()
    {
        if (IsBusy || SelectedInstance is null)
            return;

        try
        {
            IsBusy = true;
            var instance = SelectedInstance;
            var javaPath = instance.JavaPath;
            if (string.IsNullOrWhiteSpace(javaPath))
                javaPath = (await _settings.LoadAsync()).JavaPath;
            if (string.IsNullOrWhiteSpace(javaPath))
            {
                StatusText = "Явный путь Java не задан. Укажи java.exe/javaw.exe в сборке или общих настройках; иначе Java будет выбрана автоматически при запуске.";
                return;
            }

            var runtime = await _javaRuntimeInspector.InspectAsync(javaPath);
            int? requiredMajor;
            try
            {
                requiredMajor = await _versionCatalog.GetRequiredJavaMajorVersionAsync(instance.MinecraftVersion);
            }
            catch (Exception metadataError)
            {
                _logger.LogInformation(metadataError, "Не удалось получить требование Java для Minecraft {Version}", instance.MinecraftVersion);
                requiredMajor = null;
            }

            StatusText = requiredMajor is null
                ? $"Java {runtime.MajorVersion} ({runtime.VersionText}) запускается. Требование Minecraft {instance.MinecraftVersion} не удалось определить."
                : runtime.MajorVersion < requiredMajor
                    ? $"Java {runtime.MajorVersion} слишком старая для Minecraft {instance.MinecraftVersion}; требуется Java {requiredMajor} или новее."
                    : $"Java {runtime.MajorVersion} ({runtime.VersionText}) запускается и соответствует минимуму Java {requiredMajor} для Minecraft {instance.MinecraftVersion}.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось проверить Java для выбранного инстанса");
            StatusText = $"Проверка Java не пройдена: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(MinecraftInstance? instance)
    {
        if (IsBusy || instance is null)
            return;

        try
        {
            IsBusy = true;

            instance.IsFavorite = !instance.IsFavorite;

            await _service.UpdateAsync(instance);
            await RefreshItemsAsync();

            StatusText = instance.IsFavorite
                ? "Добавлено в избранное."
                : "Удалено из избранного.";
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось изменить избранное: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (IsBusy || SelectedInstance is null)
            return;

        var instance = SelectedInstance;
        var answer = System.Windows.MessageBox.Show(
            $"Удалить сборку «{instance.Name}»?\n\nДа — удалить также игровую папку и файлы.\nНет — удалить только запись, файлы сохранить.\nОтмена — ничего не менять.",
            "Удаление сборки",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Warning);
        if (answer == System.Windows.MessageBoxResult.Cancel)
            return;
        var deleteFiles = answer == System.Windows.MessageBoxResult.Yes;

        try
        {
            IsBusy = true;

            await _service.DeleteAsync(instance.Id, deleteFiles);
            Instances.Remove(instance);
            SelectedInstance = null;

            StatusText = deleteFiles
                ? $"Сборка «{instance.Name}» и её файлы удалены."
                : $"Запись «{instance.Name}» удалена; игровые файлы сохранены.";
        }
        catch (Exception ex)
        {
            StatusText = $"Не удалось удалить сборку: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DuplicateSelectedAsync()
    {
        if (IsBusy || SelectedInstance is null)
            return;

        var source = SelectedInstance;
        var answer = System.Windows.MessageBox.Show(
            $"Создать чистую копию «{source.Name}»? Настройки профиля сохранятся, папка игры будет новой и пустой.",
            "Дублирование сборки",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        try
        {
            IsBusy = true;
            StatusText = "Создание чистой копии…";
            var duplicate = await _service.DuplicateAsync(source);
            Instances.Add(duplicate);
            SelectedInstance = duplicate;
            StatusText = $"Создана чистая копия «{duplicate.Name}». Игровые файлы будут загружены при первом запуске.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось дублировать Minecraft instance {InstanceId}", source.Id);
            StatusText = $"Не удалось дублировать сборку: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSelectedFolder()
    {
        if (SelectedInstance is not { } instance) return;
        try
        {
            var path = Path.GetFullPath(instance.GameDirectory);
            if (!Directory.Exists(path))
            {
                StatusText = "Папка игры пока не создана. Она появится при первом запуске сборки.";
                return;
            }

            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
            StatusText = $"Открыта папка «{instance.Name}».";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open game directory for instance {InstanceId}", instance.Id);
            StatusText = $"Не удалось открыть папку игры: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportSelectedManifestAsync()
    {
        if (SelectedInstance is not { } instance) return;

        var safeName = string.Concat(instance.Name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт манифеста сборки",
            FileName = string.IsNullOrWhiteSpace(safeName) ? "SubReel-instance" : safeName,
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "Манифест SubReel (*.json)|*.json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var manifest = new InstanceExportManifest(
                1,
                instance.Id,
                instance.Name,
                instance.MinecraftVersion,
                instance.LoaderType,
                instance.LoaderVersion,
                instance.MemoryMb,
                instance.Source,
                instance.ModpackManifest,
                instance.CreatedAt,
                instance.LastPlayedAt,
                await CollectModFilesAsync(instance.GameDirectory),
                DateTimeOffset.UtcNow);
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            jsonOptions.Converters.Add(new JsonStringEnumConverter());
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(manifest, jsonOptions));
            StatusText = $"Манифест сборки сохранён: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not export manifest for instance {InstanceId}", instance.Id);
            StatusText = $"Не удалось экспортировать манифест: {ex.Message}";
        }
    }

    private sealed record InstanceExportManifest(
        int FormatVersion,
        Guid Id,
        string Name,
        string MinecraftVersion,
        MinecraftLoaderType LoaderType,
        string? LoaderVersion,
        int MemoryMb,
        string Source,
        InstalledModpackManifest? ModpackManifest,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastPlayedAt,
        IReadOnlyList<InstanceExportFile> Mods,
        DateTimeOffset ExportedAt);

    [RelayCommand]
    private async Task ImportModpackManifestAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт манифеста модпака SubReel",
            Filter = "Манифест SubReel (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            StatusText = "Читаю манифест модпака…";
            var fileInfo = new FileInfo(dialog.FileName);
            if (fileInfo.Length is <= 0 or > 1024 * 1024)
                throw new InvalidDataException("Размер манифеста должен быть не больше 1 МБ.");

            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            var manifest = JsonSerializer.Deserialize<InstanceExportManifest>(await File.ReadAllTextAsync(dialog.FileName), options)
                ?? throw new InvalidDataException("Файл манифеста пуст или повреждён.");
            if (manifest.FormatVersion != 1 || manifest.ModpackManifest is not { } pack ||
                string.IsNullOrWhiteSpace(pack.Provider) || string.IsNullOrWhiteSpace(pack.ProjectId) ||
                string.IsNullOrWhiteSpace(pack.VersionId) || string.IsNullOrWhiteSpace(pack.ProjectTitle))
                throw new InvalidDataException("Манифест не содержит поддерживаемую версию модпака.");

            if (!Enum.TryParse<ModpackProvider>(pack.Provider, true, out var provider) || !Enum.IsDefined(provider))
                throw new InvalidDataException($"Провайдер «{pack.Provider}» не поддерживается этой версией лаунчера.");
            var catalog = _modpackCatalogs.FirstOrDefault(item => item.Provider == provider)
                ?? throw new InvalidDataException($"Провайдер «{pack.Provider}» не поддерживается этой версией лаунчера.");
            StatusText = "Проверяю доступность версии у провайдера…";
            var versions = await catalog.GetVersionsAsync(pack.ProjectId);
            var version = versions.FirstOrDefault(item => item.Id.Equals(pack.VersionId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Исходная версия больше недоступна в каталоге. Ничего не установлено.");
            if (!version.CanInstall)
                throw new InvalidOperationException("Для исходной версии сейчас нет доступного файла установки.");

            var project = new ModpackSummary(pack.ProjectId, pack.ProjectSlug, pack.ProjectTitle, string.Empty,
                null, 0, [], catalog.Provider);
            var progress = new Progress<ModpackInstallProgress>(update => StatusText = update.Message);
            var installed = catalog.Provider == ModpackProvider.CurseForge
                ? await _curseForgeInstaller.InstallAsync(project, version, manifest.MemoryMb, progress)
                : await _modrinthInstaller.InstallAsync(project, version, manifest.MemoryMb, progress);

            await RefreshItemsAsync();
            SelectedInstance = Instances.FirstOrDefault(item => item.Id == installed.InstanceId);
            StatusText = $"Модпак «{installed.Name}» импортирован в новый инстанс. Исходные сборки не изменены.";
            if (!string.IsNullOrWhiteSpace(installed.Warning)) StatusText += $" {installed.Warning}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not import modpack manifest from {ManifestPath}", dialog.FileName);
            StatusText = $"Не удалось импортировать манифест: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private static async Task<IReadOnlyList<InstanceExportFile>> CollectModFilesAsync(string gameDirectory)
    {
        var modsDirectory = Path.Combine(Path.GetFullPath(gameDirectory), "mods");
        if (!Directory.Exists(modsDirectory)) return [];

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = false
        };
        var files = new List<InstanceExportFile>();
        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*", enumerationOptions))
        {
            var relativePath = Path.GetRelativePath(modsDirectory, path);
            if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
                relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream);
            files.Add(new InstanceExportFile(
                relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                stream.Length,
                Convert.ToHexString(hash)));
        }

        return files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private sealed record InstanceExportFile(string Path, long Size, string Sha256);

    [RelayCommand]
    private async Task LaunchSelectedAsync()
    {
        if (IsBusy || SelectedInstance is null)
            return;

        var instance = SelectedInstance;
        try
        {
            IsBusy = true;
            IsLaunching = true;
            LaunchProgress = 0;
            StatusText = "Подготовка Minecraft…";

            using var launchCancellation = new CancellationTokenSource();
            _launchCancellation = launchCancellation;
            if (instance.LoaderType == MinecraftLoaderType.Quilt)
            {
                var quilt = (await _loaderCatalog.GetLoadersAsync(instance.MinecraftVersion, launchCancellation.Token))
                    .FirstOrDefault(option => option.Id.Equals("Quilt", StringComparison.OrdinalIgnoreCase) && option.IsAvailable);
                if (!string.IsNullOrWhiteSpace(quilt?.Version) && !string.Equals(instance.LoaderVersion, quilt.Version, StringComparison.Ordinal))
                {
                    instance.LoaderVersion = quilt.Version;
                    await _service.UpdateAsync(instance, launchCancellation.Token);
                    StatusText = $"Обновлена версия Quilt до {quilt.Version}. Продолжаю запуск…";
                }
            }
            var progress = new Progress<LaunchProgress>(update =>
            {
                LaunchProgress = update.Fraction * 100;
                StatusText = update.Message;
            });

            var session = await _authService.GetActiveSessionAsync(launchCancellation.Token);
            await _launchOrchestrator.LaunchAsync(
                instance,
                session,
                _authService.ActiveAccount?.Username ?? PlayerName,
                progress,
                launchCancellation.Token);
            StatusText = $"Minecraft {instance.MinecraftVersion} запущен.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Запуск отменён.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось запустить Minecraft instance {InstanceId} ({Version}, {Loader})", instance.Id, instance.MinecraftVersion, instance.LoaderType);
            StatusText = $"Не удалось запустить игру: {DescribeFailure(ex)}";
        }
        finally
        {
            IsLaunching = false;
            _launchCancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelLaunch() => _launchCancellation?.Cancel();

    partial void OnSelectedLoaderChanged(MinecraftLoaderType value)
    {
        if (value == MinecraftLoaderType.Vanilla)
            LoaderVersion = "";
    }

    partial void OnMinecraftVersionChanged(string value) => LoaderVersion = "";
    [ObservableProperty]
    private bool _hasSelection;

    partial void OnSelectedInstanceChanged(MinecraftInstance? value)
    {
        HasSelection = value is not null;
        _initialSelectedLoaderType = value?.LoaderType;
        _initialSelectedMinecraftVersion = value?.MinecraftVersion;
    }

    private async Task RefreshItemsAsync()
    {
        var items = await _service.GetAllAsync();

        Instances.Clear();

        foreach (var item in items)
            Instances.Add(item);
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
}
