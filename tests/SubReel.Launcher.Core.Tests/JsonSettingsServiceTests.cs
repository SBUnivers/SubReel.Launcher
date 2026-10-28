using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.Infrastructure.Persistence;
using Xunit;

namespace SubReel.Launcher.Core.Tests;

public sealed class JsonSettingsServiceTests
{
    [Fact]
    public async Task LegacySettingsWithoutSchemaAreMigratedAndSavedWithoutLosingValues()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        paths.EnsureCreated();
        const string legacyJson = "{\"DefaultMemoryMb\":6144,\"Language\":\"en-US\",\"ReduceAnimations\":true}";
        await File.WriteAllTextAsync(paths.SettingsFile, legacyJson);
        var service = CreateService(paths);

        var settings = await service.LoadAsync();
        using var migratedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal(6144, settings.DefaultMemoryMb);
        Assert.Equal("en-US", settings.Language);
        Assert.True(settings.ReduceAnimations);
        Assert.Equal(1, migratedDocument.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public async Task NewerSchemaIsRejectedWithoutChangingOriginalSettingsFile()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        paths.EnsureCreated();
        const string futureJson = "{\"SchemaVersion\":999,\"DefaultMemoryMb\":8192}";
        await File.WriteAllTextAsync(paths.SettingsFile, futureJson);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateService(paths).LoadAsync());

        Assert.Equal(futureJson, await File.ReadAllTextAsync(paths.SettingsFile));
        Assert.Empty(Directory.EnumerateFiles(paths.Data, "settings.json.corrupt-*"));
    }

    [Fact]
    public async Task CorruptSettingsAreMovedToBackupAndDefaultsAreReturned()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        paths.EnsureCreated();
        const string invalidJson = "{ not-json";
        await File.WriteAllTextAsync(paths.SettingsFile, invalidJson);

        var settings = await CreateService(paths).LoadAsync();

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal(4096, settings.DefaultMemoryMb);
        Assert.False(File.Exists(paths.SettingsFile));
        var backup = Assert.Single(Directory.EnumerateFiles(paths.Data, "settings.json.corrupt-*"));
        Assert.Equal(invalidJson, await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task UpdateClampsOutOfRangeSettingsBeforePersisting()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var service = CreateService(paths);

        await service.UpdateAsync(settings =>
        {
            settings.DefaultMemoryMb = 128;
            settings.MaxConcurrentDownloads = 100;
            settings.TextScale = double.NaN;
        });
        var restored = await CreateService(paths).LoadAsync();

        Assert.Equal(512, restored.DefaultMemoryMb);
        Assert.Equal(32, restored.MaxConcurrentDownloads);
        Assert.Equal(1d, restored.TextScale);
    }

    private static JsonSettingsService CreateService(IAppPaths paths) =>
        new(paths, NullLogger<JsonSettingsService>.Instance);

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
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"subreel-settings-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
