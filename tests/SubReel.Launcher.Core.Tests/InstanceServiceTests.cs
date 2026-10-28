using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Models;
using Xunit;

namespace SubReel.Launcher.Core.Tests;

public sealed class InstanceServiceTests
{
    [Fact]
    public async Task CreateTrimsFieldsAndPersistsInstanceInsideItsOwnDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var repository = new InMemoryInstanceRepository();
        var service = new InstanceService(repository, paths, NullLogger<InstanceService>.Instance);

        var instance = await service.CreateAsync("  My pack  ", "  1.21.1  ", MinecraftLoaderType.Fabric, "  0.16.9  ", 4096);

        Assert.Equal("My pack", instance.Name);
        Assert.Equal("1.21.1", instance.MinecraftVersion);
        Assert.Equal("0.16.9", instance.LoaderVersion);
        Assert.Equal(Path.Combine(paths.Instances, instance.Id.ToString("N"), "game"), instance.GameDirectory);
        Assert.True(Directory.Exists(instance.GameDirectory));
        Assert.Same(instance, await repository.GetByIdAsync(instance.Id));
    }

    [Fact]
    public async Task CreateRejectsInvalidMemoryWithoutPersistingAnInstance()
    {
        using var temporary = new TemporaryDirectory();
        var repository = new InMemoryInstanceRepository();
        var service = new InstanceService(repository, new TestAppPaths(temporary.Path), NullLogger<InstanceService>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CreateAsync("Pack", "1.21.1", MinecraftLoaderType.Vanilla, null, 256));

        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public void ValidatedGameDirectoryRejectsPathOutsideInstanceRoot()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var service = new InstanceService(new InMemoryInstanceRepository(), paths, NullLogger<InstanceService>.Instance);
        var instance = new MinecraftInstance
        {
            Id = Guid.NewGuid(),
            GameDirectory = Path.Combine(temporary.Path, "outside-game")
        };

        Assert.Throws<InvalidOperationException>(() => service.GetValidatedGameDirectory(instance));
    }

    [Fact]
    public async Task UpdateCannotRedirectGameDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var repository = new InMemoryInstanceRepository();
        var service = new InstanceService(repository, paths, NullLogger<InstanceService>.Instance);
        var instance = await service.CreateAsync("Pack", "1.21.1", MinecraftLoaderType.Vanilla, null, 4096);
        instance.GameDirectory = Path.Combine(temporary.Path, "redirected");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(instance));
    }

    [Fact]
    public async Task DuplicateCopiesOnlyRequestedUserDataAndGetsAnIsolatedGameDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var repository = new InMemoryInstanceRepository();
        var service = new InstanceService(repository, paths, NullLogger<InstanceService>.Instance);
        var source = await service.CreateAsync("Source", "1.21.1", MinecraftLoaderType.Fabric, "0.16.9", 4096);
        var saveFile = Path.Combine(source.GameDirectory, "saves", "World", "level.dat");
        var modFile = Path.Combine(source.GameDirectory, "mods", "example.jar");
        var optionsFile = Path.Combine(source.GameDirectory, "options.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(saveFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modFile)!);
        await File.WriteAllTextAsync(saveFile, "world data");
        await File.WriteAllTextAsync(modFile, "mod binary placeholder");
        await File.WriteAllTextAsync(optionsFile, "video settings");

        var duplicate = await service.DuplicateAsync(source, new InstanceCopyOptions(Saves: true, GameOptions: true));

        Assert.NotEqual(source.Id, duplicate.Id);
        Assert.NotEqual(source.GameDirectory, duplicate.GameDirectory);
        Assert.Equal("world data", await File.ReadAllTextAsync(Path.Combine(duplicate.GameDirectory, "saves", "World", "level.dat")));
        Assert.Equal("video settings", await File.ReadAllTextAsync(Path.Combine(duplicate.GameDirectory, "options.txt")));
        Assert.False(File.Exists(Path.Combine(duplicate.GameDirectory, "mods", "example.jar")));
        Assert.Equal(source.LoaderType, duplicate.LoaderType);
        Assert.Equal(source.LoaderVersion, duplicate.LoaderVersion);
    }

    [Fact]
    public async Task RecoveryRestoresStagedFilesWhenCatalogRecordStillExists()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var repository = new InMemoryInstanceRepository();
        var service = new InstanceService(repository, paths, NullLogger<InstanceService>.Instance);
        var instance = await service.CreateAsync("Interrupted delete", "1.21.1", MinecraftLoaderType.Vanilla, null, 4096);
        var markerPath = Path.Combine(instance.GameDirectory, "keep.txt");
        await File.WriteAllTextAsync(markerPath, "preserve instance data");
        var instanceRoot = Path.Combine(paths.Instances, instance.Id.ToString("N"));
        var stagedRoot = Path.Combine(paths.Instances, $".deleting-{instance.Id:N}-{Guid.NewGuid():N}");
        Directory.Move(instanceRoot, stagedRoot);

        await service.RecoverInterruptedModpackInstallsAsync();

        Assert.True(File.Exists(Path.Combine(instance.GameDirectory, "keep.txt")));
        Assert.Equal("preserve instance data", await File.ReadAllTextAsync(markerPath));
        Assert.False(Directory.Exists(stagedRoot));
        Assert.NotNull(await repository.GetByIdAsync(instance.Id));
    }

    [Fact]
    public async Task RecoveryRemovesStagedFilesWhenCatalogRecordWasAlreadyDeleted()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var repository = new InMemoryInstanceRepository();
        var service = new InstanceService(repository, paths, NullLogger<InstanceService>.Instance);
        var instance = await service.CreateAsync("Deleted instance", "1.21.1", MinecraftLoaderType.Vanilla, null, 4096);
        var instanceRoot = Path.Combine(paths.Instances, instance.Id.ToString("N"));
        var stagedRoot = Path.Combine(paths.Instances, $".deleting-{instance.Id:N}-{Guid.NewGuid():N}");
        Directory.Move(instanceRoot, stagedRoot);
        await repository.DeleteAsync(instance.Id);

        await service.RecoverInterruptedModpackInstallsAsync();

        Assert.False(Directory.Exists(stagedRoot));
        Assert.Null(await repository.GetByIdAsync(instance.Id));
    }

    private sealed class InMemoryInstanceRepository : IInstanceRepository
    {
        private readonly Dictionary<Guid, MinecraftInstance> _instances = [];

        public Task<IReadOnlyList<MinecraftInstance>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MinecraftInstance>>(_instances.Values.ToArray());

        public Task<MinecraftInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_instances.GetValueOrDefault(id));

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
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"subreel-core-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
