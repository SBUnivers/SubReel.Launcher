using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Models;
using SubReel.Launcher.Infrastructure.Persistence;
using Xunit;

namespace SubReel.Launcher.Core.Tests;

public sealed class JsonInstanceRepositoryTests
{
    [Fact]
    public async Task SaveSurvivesRepositoryRecreationAndWritesInstanceManifest()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var instance = CreateInstance(paths, "Persisted pack");
        var repository = CreateRepository(paths);

        await repository.SaveAsync(instance);
        var restored = Assert.Single(await CreateRepository(paths).GetAllAsync());

        Assert.Equal(instance.Id, restored.Id);
        Assert.Equal(instance.GameDirectory, restored.GameDirectory);
        Assert.True(File.Exists(Path.Combine(paths.Instances, instance.Id.ToString("N"), "instance.json")));
    }

    [Fact]
    public async Task CorruptCatalogIsPreservedAsBackupAndReadAsEmpty()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        paths.EnsureCreated();
        var catalogPath = Path.Combine(paths.Data, "instances.json");
        const string corruptJson = "{ definitely not valid json";
        await File.WriteAllTextAsync(catalogPath, corruptJson);

        var instances = await CreateRepository(paths).GetAllAsync();

        Assert.Empty(instances);
        Assert.False(File.Exists(catalogPath));
        var backupPath = Assert.Single(Directory.EnumerateFiles(paths.Data, "instances.json.corrupt-*"));
        Assert.Equal(corruptJson, await File.ReadAllTextAsync(backupPath));
    }

    [Fact]
    public async Task LegacyArraySkipsNullEmptyAndDuplicateIdsWithoutRewritingSource()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        paths.EnsureCreated();
        var valid = CreateInstance(paths, "Valid legacy row");
        var emptyId = new MinecraftInstance { Id = Guid.Empty, Name = "Empty ID" };
        var duplicateId = new MinecraftInstance { Id = valid.Id, Name = "Duplicate ID" };
        var source = JsonSerializer.Serialize(new MinecraftInstance?[] { valid, null, emptyId, duplicateId });
        var catalogPath = Path.Combine(paths.Data, "instances.json");
        await File.WriteAllTextAsync(catalogPath, source);

        var instances = await CreateRepository(paths).GetAllAsync();

        var restored = Assert.Single(instances);
        Assert.Equal(valid.Id, restored.Id);
        Assert.Equal("Valid legacy row", restored.Name);
        Assert.Equal(source, await File.ReadAllTextAsync(catalogPath));
    }

    [Fact]
    public async Task SaveRejectsGameDirectoryOutsideItsInstanceFolderBeforeCatalogWrite()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        paths.EnsureCreated();
        var instance = CreateInstance(paths, "Invalid path");
        instance.GameDirectory = Path.Combine(temporary.Path, "outside", "game");

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(paths).SaveAsync(instance));

        Assert.False(File.Exists(Path.Combine(paths.Data, "instances.json")));
        Assert.False(Directory.Exists(Path.Combine(paths.Instances, instance.Id.ToString("N"))));
    }

    [Fact]
    public async Task GetAllSortsFavoritesThenRecentlyPlayedThenName()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var repository = CreateRepository(paths);
        var olderFavorite = CreateInstance(paths, "Z favorite", favorite: true, lastPlayedAt: DateTimeOffset.UtcNow.AddDays(-2));
        var recentFavorite = CreateInstance(paths, "A favorite", favorite: true, lastPlayedAt: DateTimeOffset.UtcNow.AddDays(-1));
        var recentNormal = CreateInstance(paths, "B normal", lastPlayedAt: DateTimeOffset.UtcNow);
        var neverPlayed = CreateInstance(paths, "C normal");

        foreach (var instance in new[] { neverPlayed, recentNormal, olderFavorite, recentFavorite })
            await repository.SaveAsync(instance);

        var ordered = await repository.GetAllAsync();

        Assert.Equal(new[] { recentFavorite.Id, olderFavorite.Id, recentNormal.Id, neverPlayed.Id }, ordered.Select(instance => instance.Id));
    }

    private static JsonInstanceRepository CreateRepository(IAppPaths paths) =>
        new(paths, NullLogger<JsonInstanceRepository>.Instance);

    private static MinecraftInstance CreateInstance(
        TestAppPaths paths,
        string name,
        bool favorite = false,
        DateTimeOffset? lastPlayedAt = null)
    {
        var id = Guid.NewGuid();
        return new MinecraftInstance
        {
            Id = id,
            Name = name,
            MinecraftVersion = "1.21.1",
            GameDirectory = Path.Combine(paths.Instances, id.ToString("N"), "game"),
            IsFavorite = favorite,
            LastPlayedAt = lastPlayedAt
        };
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
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"subreel-repository-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
