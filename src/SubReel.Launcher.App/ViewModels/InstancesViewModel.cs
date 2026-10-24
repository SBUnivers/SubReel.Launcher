
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Launching;
using SubReel.Launcher.Core.Models;

namespace SubReel.Launcher.App.ViewModels;

public partial class InstancesViewModel : ObservableObject
{
    private readonly InstanceService _service;
    private readonly ILaunchOrchestrator _launchOrchestrator;
    private readonly IGameAuthService _authService;

    public ObservableCollection<MinecraftInstance> Instances { get; } = [];

    public MinecraftLoaderType[] LoaderTypes { get; } =
        [MinecraftLoaderType.Vanilla, MinecraftLoaderType.Fabric,
         MinecraftLoaderType.Forge, MinecraftLoaderType.NeoForge];

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
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLaunching;

    private CancellationTokenSource? _launchCancellation;

    [ObservableProperty]
    private double _launchProgress;

    [ObservableProperty]
    private string _playerName = "SubReelPlayer";

    public InstancesViewModel(InstanceService service, ILaunchOrchestrator launchOrchestrator, IGameAuthService authService)
    {
        _service = service;
        _launchOrchestrator = launchOrchestrator;
        _authService = authService;
    }

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

            var instance = await _service.CreateAsync(
                InstanceName,
                MinecraftVersion,
                SelectedLoader,
                LoaderVersion,
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

            var instanceName = SelectedInstance.Name;
            await _service.UpdateAsync(SelectedInstance);
            await RefreshItemsAsync();

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

        try
        {
            IsBusy = true;

            await _service.DeleteAsync(instance.Id);
            Instances.Remove(instance);
            SelectedInstance = null;

            StatusText = $"Запись «{instance.Name}» удалена.";
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
            StatusText = $"Не удалось запустить игру: {ex.Message}";
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
    [ObservableProperty]
    private bool _hasSelection;

    partial void OnSelectedInstanceChanged(MinecraftInstance? value)
    {
        HasSelection = value is not null;
    }

    private async Task RefreshItemsAsync()
    {
        var items = await _service.GetAllAsync();

        Instances.Clear();

        foreach (var item in items)
            Instances.Add(item);
    }
}
