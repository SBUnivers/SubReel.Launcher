using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.App.Services;
using SubReel.Launcher.Rust.Abstractions.Models;
using SubReel.Launcher.Rust.Abstractions.Services;
using SubReel.Launcher.Rust.Core.Instances;

namespace SubReel.Launcher.App.ViewModels;

public partial class RustClientsViewModel : ObservableObject
{
    private readonly IRustClientCatalog _catalog;
    private readonly IRustClientInstaller _installer;
    private readonly IRustClientLauncher _launcher;
    private readonly RustInstanceService _instances;
    private readonly ILocalizationService _localization;
    private readonly IConfirmationService _confirmation;
    private readonly ILogger<RustClientsViewModel> _logger;
    private CancellationTokenSource? _installCancellation;

    public ObservableCollection<RustClientRelease> Releases { get; } = [];
    public ObservableCollection<RustInstanceRecord> Instances { get; } = [];

    [ObservableProperty] private RustClientRelease? selectedRelease;
    [ObservableProperty] private RustInstanceRecord? selectedInstance;
    [ObservableProperty] private string instanceName = "Pomme";
    [ObservableProperty] private string selectedInstanceName = string.Empty;
    [ObservableProperty] private string username = string.Empty;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isInstalling;

    public RustClientsViewModel(
        IRustClientCatalog catalog,
        IRustClientInstaller installer,
        IRustClientLauncher launcher,
        RustInstanceService instances,
        ILocalizationService localization,
        IConfirmationService confirmation,
        ILogger<RustClientsViewModel> logger)
    {
        _catalog = catalog;
        _installer = installer;
        _launcher = launcher;
        _instances = instances;
        _localization = localization;
        _confirmation = confirmation;
        _logger = logger;
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusText = _localization.Get("Rust.Status.Loading", "Загружаю клиент Pomme и инстансы…");
            var releases = await _catalog.GetReleasesAsync("pomme");
            var instances = await _instances.GetAllAsync();
            Releases.Clear();
            foreach (var release in releases) Releases.Add(release);
            Instances.Clear();
            foreach (var instance in instances.Where(item => item.ClientId.Equals("pomme", StringComparison.OrdinalIgnoreCase)))
                Instances.Add(instance);
            SelectedRelease ??= Releases.FirstOrDefault();
            SelectedInstance ??= Instances.FirstOrDefault();
            StatusText = Releases.Count == 0
                ? _localization.Get("Rust.Status.NoReleases", "Не удалось найти релизы Pomme.")
                : string.Format(_localization.Get("Rust.Status.Ready", "Доступно релизов: {0}. Выбери релиз для установки."), Releases.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось загрузить Rust-клиент Pomme");
            StatusText = string.Format(_localization.Get("Rust.Status.LoadFailed", "Не удалось загрузить Pomme: {0}"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (SelectedRelease is null || IsBusy) return;
        IsBusy = true;
        IsInstalling = true;
        ProgressValue = 0;
        using var cancellation = new CancellationTokenSource();
        _installCancellation = cancellation;
        try
        {
            StatusText = _localization.Get("Rust.Status.Installing", "Устанавливаю клиент…");
            var progress = new Progress<RustInstallProgress>(value =>
            {
                if (value.Stage == RustInstallStage.Downloading && value.BytesTotal > 0)
                    ProgressValue = Math.Clamp((double)value.BytesCompleted / value.BytesTotal * 100, 0, 100);
                StatusText = value.Stage switch
                {
                    RustInstallStage.Downloading => string.Format(_localization.Get("Rust.Status.Downloading", "Загрузка: {0}%"), (int)ProgressValue),
                    RustInstallStage.Verifying => _localization.Get("Rust.Status.Verifying", "Проверяю SHA-256 архива…"),
                    RustInstallStage.Extracting => string.Format(_localization.Get("Rust.Status.Extracting", "Распаковываю: {0}"), value.FileName ?? ""),
                    _ => _localization.Get("Rust.Status.Registering", "Сохраняю установленный клиент…")
                };
            });
            await _installer.InstallAsync(SelectedRelease, progress, cancellation.Token);
            var name = string.IsNullOrWhiteSpace(InstanceName) ? "Pomme" : InstanceName.Trim();
            var instance = await _instances.CreateAsync("pomme", name, SelectedRelease.Tag, cancellation.Token);
            Instances.Insert(0, instance);
            SelectedInstance = instance;
            StatusText = string.Format(_localization.Get("Rust.Status.Installed", "Клиент установлен. Создан инстанс «{0}»."), instance.Name);
        }
        catch (OperationCanceledException) { StatusText = _localization.Get("Rust.Status.Cancelled", "Установка отменена."); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось установить Pomme {ReleaseTag}", SelectedRelease.Tag);
            StatusText = string.Format(_localization.Get("Rust.Status.InstallFailed", "Не удалось установить клиент: {0}"), ex.Message);
        }
        finally
        {
            _installCancellation = null;
            IsInstalling = false;
            IsBusy = false;
            InstallCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelInstall))]
    private void CancelInstall() => _installCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (SelectedInstance is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _launcher.LaunchAsync(new RustLaunchRequest(
                SelectedInstance,
                string.IsNullOrWhiteSpace(Username) ? null : Username.Trim()));
            try
            {
                var updated = await _instances.MarkPlayedAsync(SelectedInstance.Id);
                if (updated is not null)
                {
                    var index = Instances.IndexOf(SelectedInstance);
                    if (index >= 0) Instances[index] = updated;
                    SelectedInstance = updated;
                }
            }
            catch (Exception persistenceException)
            {
                _logger.LogWarning(persistenceException, "Pomme запустился, но время последней игры не сохранено");
            }
            StatusText = string.Format(_localization.Get("Rust.Status.Launched", "Pomme запущен. PID процесса: {0}."), result.ProcessId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось запустить Rust-инстанс {InstanceId}", SelectedInstance.Id);
            StatusText = string.Format(_localization.Get("Rust.Status.LaunchFailed", "Не удалось запустить Pomme: {0}"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameInstanceAsync()
    {
        if (SelectedInstance is null || IsBusy) return;
        try
        {
            var updated = await _instances.RenameAsync(SelectedInstance.Id, SelectedInstanceName);
            var index = Instances.IndexOf(SelectedInstance);
            if (index >= 0) Instances[index] = updated;
            SelectedInstance = updated;
            StatusText = _localization.Get("Rust.Status.Renamed", "Название инстанса сохранено.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось переименовать Rust-инстанс {InstanceId}", SelectedInstance.Id);
            StatusText = string.Format(_localization.Get("Rust.Status.RenameFailed", "Не удалось переименовать инстанс: {0}"), ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteInstance))]
    private async Task DeleteInstanceAsync()
    {
        if (SelectedInstance is null || IsBusy) return;
        var instance = SelectedInstance;
        if (!_confirmation.Confirm(
                _localization.Get("Rust.Delete.Title", "Удалить Rust-инстанс?"),
                string.Format(_localization.Get("Rust.Delete.Message", "Инстанс «{0}» и его игровые данные будут удалены. Установленный клиент Pomme останется."), instance.Name)))
            return;
        if (_launcher.IsClientRunning(instance))
        {
            StatusText = _localization.Get("Rust.Status.RunningCannotDelete", "Закрой Pomme, чтобы удалить этот инстанс.");
            return;
        }

        IsBusy = true;
        try
        {
            await _instances.DeleteAsync(instance.Id);
            Instances.Remove(instance);
            SelectedInstance = Instances.FirstOrDefault();
            StatusText = string.Format(_localization.Get("Rust.Status.Deleted", "Инстанс «{0}» удалён."), instance.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось удалить Rust-инстанс {InstanceId}", instance.Id);
            StatusText = string.Format(_localization.Get("Rust.Status.DeleteFailed", "Не удалось удалить инстанс: {0}"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    private bool CanInstall() => SelectedRelease is not null && !IsBusy;
    private bool CanCancelInstall() => IsInstalling;
    private bool CanLaunch() => SelectedInstance is not null && !IsBusy;
    private bool CanRename() => SelectedInstance is not null && !IsBusy && !string.IsNullOrWhiteSpace(SelectedInstanceName);
    private bool CanDeleteInstance() => SelectedInstance is not null && !IsBusy;

    partial void OnSelectedReleaseChanged(RustClientRelease? value) => InstallCommand.NotifyCanExecuteChanged();
    partial void OnSelectedInstanceChanged(RustInstanceRecord? value)
    {
        SelectedInstanceName = value?.Name ?? string.Empty;
        LaunchCommand.NotifyCanExecuteChanged();
        RenameInstanceCommand.NotifyCanExecuteChanged();
        DeleteInstanceCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedInstanceNameChanged(string value) => RenameInstanceCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value)
    {
        InstallCommand.NotifyCanExecuteChanged();
        LaunchCommand.NotifyCanExecuteChanged();
        RenameInstanceCommand.NotifyCanExecuteChanged();
        DeleteInstanceCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsInstallingChanged(bool value) => CancelInstallCommand.NotifyCanExecuteChanged();
}
