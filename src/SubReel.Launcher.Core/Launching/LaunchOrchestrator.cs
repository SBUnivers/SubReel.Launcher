using System.Diagnostics;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Models;

namespace SubReel.Launcher.Core.Launching;

public enum LaunchStage { Resolving, InstallingLoader, DownloadingFiles, StartingGame, Running }

public sealed record LaunchProgress(LaunchStage Stage, string Message, double Fraction = 0);

public interface ILaunchOrchestrator
{
    Task<Process> LaunchAsync(
        MinecraftInstance instance,
        GameSession? account,
        string offlinePlayerName,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Coordinates the game adapter and instance lifecycle without depending on WPF or CmlLib.</summary>
public sealed class LaunchOrchestrator(IGameService gameService, InstanceService instances) : ILaunchOrchestrator
{
    public async Task<Process> LaunchAsync(
        MinecraftInstance instance,
        GameSession? account,
        string offlinePlayerName,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new LaunchProgress(LaunchStage.Resolving, $"Подготовка {instance.MinecraftVersion}…"));

        var installProgress = new Progress<GameInstallProgress>(update =>
        {
            var stage = update.Stage.Contains("установ", StringComparison.OrdinalIgnoreCase)
                ? LaunchStage.InstallingLoader
                : update.Stage.Contains("запущ", StringComparison.OrdinalIgnoreCase)
                    ? LaunchStage.Running
                    : update.Stage.Contains("запуск", StringComparison.OrdinalIgnoreCase)
                        ? LaunchStage.StartingGame
                    : LaunchStage.DownloadingFiles;
            progress?.Report(new LaunchProgress(stage, update.Stage, update.Fraction));
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
                instance.JavaPath,
                instance.JvmArguments),
            installProgress,
            cancellationToken);

        instance.LastPlayedAt = DateTimeOffset.UtcNow;
        await instances.UpdateAsync(instance, cancellationToken);
        progress?.Report(new LaunchProgress(LaunchStage.Running, "Minecraft запущен.", 1));
        return process;
    }
}
