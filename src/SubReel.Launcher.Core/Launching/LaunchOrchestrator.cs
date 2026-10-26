using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Abstractions.Settings;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Models;

namespace SubReel.Launcher.Core.Launching;

public enum LaunchStage { Resolving, InstallingLoader, DownloadingFiles, StartingGame, Running }
public enum LaunchState { Preparing, InstallingLoader, Downloading, Starting, Running, Exited, Crashed, Canceled, Failed }

public sealed record LaunchProgress(LaunchStage Stage, string Message, double Fraction = 0);
public sealed record LaunchStatus(Guid InstanceId, string InstanceName, LaunchState State,
    string Message, LaunchStage? Stage = null, int? ProcessId = null, int? ExitCode = null,
    DateTimeOffset? StartedAt = null, string? CrashReportPath = null);

public interface ILaunchOrchestrator
{
    event Action<LaunchStatus>? StatusChanged;
    IReadOnlyCollection<LaunchStatus> ActiveLaunches { get; }

    Task<Process> LaunchAsync(
        MinecraftInstance instance,
        GameSession? account,
        string offlinePlayerName,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Coordinates the game adapter and instance lifecycle without depending on WPF or CmlLib.</summary>
public sealed class LaunchOrchestrator(
    IGameService gameService,
    InstanceService instances,
    ISettingsService settings,
    ILogger<LaunchOrchestrator> logger) : ILaunchOrchestrator
{
    private readonly ConcurrentDictionary<Guid, LaunchStatus> _active = new();

    public event Action<LaunchStatus>? StatusChanged;
    public IReadOnlyCollection<LaunchStatus> ActiveLaunches => _active.Values.ToArray();

    public async Task<Process> LaunchAsync(
        MinecraftInstance instance,
        GameSession? account,
        string offlinePlayerName,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        cancellationToken.ThrowIfCancellationRequested();
        var initialStatus = new LaunchStatus(instance.Id, instance.Name, LaunchState.Preparing,
            $"Подготовка Minecraft {instance.MinecraftVersion}…", LaunchStage.Resolving);
        if (!_active.TryAdd(instance.Id, initialStatus))
            throw new InvalidOperationException($"Сборка «{instance.Name}» уже запускается или запущена.");

        Publish(initialStatus);
        try
        {
        var launcherSettings = await settings.LoadAsync(cancellationToken);
        progress?.Report(new LaunchProgress(LaunchStage.Resolving, $"Подготовка {instance.MinecraftVersion}…"));

        var installProgress = new InlineProgress<GameInstallProgress>(update =>
        {
            var (stage, state) = update.Stage switch
            {
                GameInstallStage.InstallingLoader => (LaunchStage.InstallingLoader, LaunchState.InstallingLoader),
                GameInstallStage.DownloadingFiles => (LaunchStage.DownloadingFiles, LaunchState.Downloading),
                GameInstallStage.StartingGame => (LaunchStage.StartingGame, LaunchState.Starting),
                GameInstallStage.Running => (LaunchStage.Running, LaunchState.Running),
                _ => (LaunchStage.Resolving, LaunchState.Preparing)
            };
            Publish(initialStatus with { State = state, Stage = stage, Message = update.Message });
            progress?.Report(new LaunchProgress(stage, update.Message, update.Fraction));
        });
        var process = await gameService.LaunchAsync(
            new GameLaunchRequest(
                instance.GameDirectory,
                instance.MinecraftVersion,
                instance.MemoryMb,
                string.IsNullOrWhiteSpace(offlinePlayerName) ? "SubReelPlayer" : offlinePlayerName,
                account,
                instance.LoaderType.ToString(),
                instance.LoaderVersion,
                instance.Id,
                string.IsNullOrWhiteSpace(instance.JavaPath) ? launcherSettings.JavaPath : instance.JavaPath,
                instance.JvmArguments),
            installProgress,
            cancellationToken);

        instance.LastPlayedAt = DateTimeOffset.UtcNow;
        try
        {
            // The game process is already running; a metadata write failure must
            // not turn a successful launch into a false failure or lose tracking.
            await instances.UpdateAsync(instance, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Minecraft instance {InstanceId} started, but LastPlayedAt could not be saved", instance.Id);
        }
        try
        {
            await settings.UpdateAsync(value => value.LastPlayedInstanceId = instance.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Minecraft instance {InstanceId} started, but LastPlayedInstanceId could not be saved", instance.Id);
        }
        var runningStatus = new LaunchStatus(instance.Id, instance.Name, LaunchState.Running,
            "Minecraft запущен.", LaunchStage.Running, process.Id, StartedAt: DateTimeOffset.UtcNow);
        Publish(runningStatus);
        _ = ObserveExitAsync(instance.Id, process, instance.GameDirectory);
        progress?.Report(new LaunchProgress(LaunchStage.Running, "Minecraft запущен.", 1));
        return process;
        }
        catch (OperationCanceledException)
        {
            Finish(instance.Id, initialStatus with { State = LaunchState.Canceled, Message = "Запуск отменён." });
            throw;
        }
        catch (Exception ex)
        {
            Finish(instance.Id, initialStatus with { State = LaunchState.Failed, Message = ex.Message });
            throw;
        }
    }

    private async Task ObserveExitAsync(Guid instanceId, Process process, string gameDirectory)
    {
        try
        {
            var exit = await gameService.WaitForExitAsync(process, gameDirectory);
            var status = _active.TryGetValue(instanceId, out var current) ? current : null;
            if (status is null) return;
            Finish(instanceId, status with
            {
                State = exit.ExitCode == 0 ? LaunchState.Exited : LaunchState.Crashed,
                Message = exit.ExitCode == 0
                    ? "Minecraft закрыт."
                    : exit.CrashReportPath is null
                        ? $"Minecraft завершился с кодом {exit.ExitCode}; crash-report не найден."
                        : $"Minecraft завершился с кодом {exit.ExitCode}. Отчёт: {Path.GetFileName(exit.CrashReportPath)}",
                ExitCode = exit.ExitCode,
                CrashReportPath = exit.CrashReportPath
            });
        }
        catch (Exception ex)
        {
            if (_active.TryGetValue(instanceId, out var status))
                Finish(instanceId, status with { State = LaunchState.Failed, Message = $"Не удалось отследить процесс: {ex.Message}" });
        }
    }

    private void Publish(LaunchStatus status)
    {
        _active[status.InstanceId] = status;
        NotifySubscribers(status);
    }

    private void Finish(Guid instanceId, LaunchStatus status)
    {
        _active.TryRemove(instanceId, out _);
        NotifySubscribers(status);
    }

    private void NotifySubscribers(LaunchStatus status)
    {
        if (StatusChanged is not { } handlers) return;
        foreach (Action<LaunchStatus> handler in handlers.GetInvocationList())
        {
            try { handler(status); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Launch status subscriber failed for instance {InstanceId}", status.InstanceId);
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
