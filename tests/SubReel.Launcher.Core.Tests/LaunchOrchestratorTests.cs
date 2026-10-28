using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.Abstractions.Settings;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Launching;
using SubReel.Launcher.Core.Models;
using Xunit;

namespace SubReel.Launcher.Core.Tests;

public sealed class LaunchOrchestratorTests
{
    [Fact]
    public async Task CancelLaunchReportsCanceledAndClearsActiveState()
    {
        using var fixture = await Fixture.CreateAsync();
        var gameStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var game = new FakeGameService
        {
            Launch = async (_, _, token) =>
            {
                gameStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Expected cancellation.");
            }
        };
        var orchestrator = fixture.CreateOrchestrator(game);
        var statuses = new List<LaunchStatus>();
        orchestrator.StatusChanged += statuses.Add;

        var launch = orchestrator.LaunchAsync(fixture.Instance, null, "Player");
        await gameStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(orchestrator.CancelLaunch(fixture.Instance.Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch);

        Assert.Empty(orchestrator.ActiveLaunches);
        Assert.Equal(LaunchState.Canceled, statuses[^1].State);
        Assert.False(orchestrator.CancelLaunch(fixture.Instance.Id));
    }

    [Fact]
    public async Task DuplicateLaunchForSameInstanceIsRejectedWhileFirstIsActive()
    {
        using var fixture = await Fixture.CreateAsync();
        var gameStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var game = new FakeGameService
        {
            Launch = async (_, _, token) =>
            {
                gameStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Expected cancellation.");
            }
        };
        var orchestrator = fixture.CreateOrchestrator(game);
        var firstLaunch = orchestrator.LaunchAsync(fixture.Instance, null, "Player");
        await gameStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.LaunchAsync(fixture.Instance, null, "Player"));

        Assert.True(orchestrator.CancelLaunch(fixture.Instance.Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstLaunch);
        Assert.Empty(orchestrator.ActiveLaunches);
    }

    [Fact]
    public async Task SuccessfulLaunchTracksRunningThenExitedAndRemovesActiveState()
    {
        using var fixture = await Fixture.CreateAsync();
        var game = new FakeGameService { Launch = (_, _, _) => Task.FromResult(Process.GetCurrentProcess()) };
        var orchestrator = fixture.CreateOrchestrator(game);
        var exited = new TaskCompletionSource<LaunchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.StatusChanged += status =>
        {
            if (status.State == LaunchState.Exited) exited.TrySetResult(status);
        };

        _ = await orchestrator.LaunchAsync(fixture.Instance, null, "Player");
        Assert.Contains(orchestrator.ActiveLaunches, status => status.InstanceId == fixture.Instance.Id && status.State == LaunchState.Running);
        Assert.NotNull(fixture.Instance.LastPlayedAt);
        Assert.Equal(fixture.Instance.Id, fixture.Settings.Value.LastPlayedInstanceId);

        game.CompleteExit(0);
        var finalStatus = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LaunchState.Exited, finalStatus.State);
        Assert.Equal(0, finalStatus.ExitCode);
        Assert.DoesNotContain(orchestrator.ActiveLaunches, status => status.InstanceId == fixture.Instance.Id);
    }

    [Fact]
    public async Task LaunchFailureReportsFailedAndClearsActiveState()
    {
        using var fixture = await Fixture.CreateAsync();
        var game = new FakeGameService
        {
            Launch = (_, _, _) => throw new InvalidOperationException("Simulated game setup failure.")
        };
        var orchestrator = fixture.CreateOrchestrator(game);
        var statuses = new List<LaunchStatus>();
        orchestrator.StatusChanged += statuses.Add;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.LaunchAsync(fixture.Instance, null, "Player"));

        Assert.Equal("Simulated game setup failure.", exception.Message);
        Assert.Empty(orchestrator.ActiveLaunches);
        Assert.Equal(LaunchState.Failed, statuses[^1].State);
    }

    private sealed class FakeGameService : IGameService
    {
        public Func<GameLaunchRequest, IProgress<GameInstallProgress>?, CancellationToken, Task<Process>> Launch { get; init; }
            = (_, _, _) => Task.FromException<Process>(new NotImplementedException());

        private readonly TaskCompletionSource<GameProcessExitInfo> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteExit(int exitCode, string? crashReportPath = null) =>
            _exit.TrySetResult(new GameProcessExitInfo(exitCode, crashReportPath));

        public Task<Process> LaunchAsync(
            GameLaunchRequest request,
            IProgress<GameInstallProgress>? progress = null,
            CancellationToken cancellationToken = default) => Launch(request, progress, cancellationToken);

        public Task<GameProcessExitInfo> WaitForExitAsync(Process process, string gameDirectory) => _exit.Task;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary;
        private readonly InMemoryInstanceRepository _repository;
        private readonly TestAppPaths _paths;

        public MinecraftInstance Instance { get; }
        public FakeSettingsService Settings { get; } = new();

        private Fixture(TemporaryDirectory temporary, InMemoryInstanceRepository repository, TestAppPaths paths, MinecraftInstance instance)
        {
            _temporary = temporary;
            _repository = repository;
            _paths = paths;
            Instance = instance;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var temporary = new TemporaryDirectory();
            var paths = new TestAppPaths(temporary.Path);
            var repository = new InMemoryInstanceRepository();
            var service = new InstanceService(repository, paths, NullLogger<InstanceService>.Instance);
            var instance = await service.CreateAsync("Launch test", "1.21.1", MinecraftLoaderType.Vanilla, null, 4096);
            return new Fixture(temporary, repository, paths, instance);
        }

        public LaunchOrchestrator CreateOrchestrator(IGameService gameService)
        {
            var instanceService = new InstanceService(_repository, _paths, NullLogger<InstanceService>.Instance);
            return new LaunchOrchestrator(gameService, instanceService, Settings, NullLogger<LaunchOrchestrator>.Instance);
        }

        public void Dispose() => _temporary.Dispose();
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public LauncherSettings Value { get; } = new();

        public Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
        {
            Copy(settings, Value);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Action<LauncherSettings> update, CancellationToken cancellationToken = default)
        {
            update(Value);
            return Task.CompletedTask;
        }

        private static void Copy(LauncherSettings from, LauncherSettings to)
        {
            to.JavaPath = from.JavaPath;
            to.LastPlayedInstanceId = from.LastPlayedInstanceId;
        }
    }

    private sealed class InMemoryInstanceRepository : IInstanceRepository
    {
        private readonly Dictionary<Guid, MinecraftInstance> _instances = [];
        public Task<IReadOnlyList<MinecraftInstance>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MinecraftInstance>>(_instances.Values.ToArray());
        public Task<MinecraftInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_instances.GetValueOrDefault(id));
        public Task SaveAsync(MinecraftInstance instance, CancellationToken cancellationToken = default)
        {
            _instances[instance.Id] = instance;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _instances.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string Root { get; } = root;
        public string Data { get; } = Path.Combine(root, "Data");
        public string SettingsFile => Path.Combine(Data, "settings.json");
        public string Instances => Path.Combine(Data, "Instances");
        public string Shared => Path.Combine(Data, "Shared");
        public string SharedVersions => Path.Combine(Shared, "versions");
        public string SharedLibraries => Path.Combine(Shared, "libraries");
        public string SharedAssets => Path.Combine(Shared, "assets");
        public string SharedRuntime => Path.Combine(Shared, "runtime");
        public string SharedResources => Path.Combine(Shared, "resources");
        public string Auth => Path.Combine(Data, "Auth");
        public string AccountsFile => Path.Combine(Auth, "accounts.json");
        public string LauncherAccountsFile => Path.Combine(Auth, "launcher-accounts.json");
        public string Cache => Path.Combine(Data, "Cache");
        public string Logs => Path.Combine(Data, "Logs");
        public string Plugins => Path.Combine(Data, "Plugins");
        public void EnsureCreated()
        {
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Instances);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"subreel-launch-tests-{Guid.NewGuid():N}");
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
