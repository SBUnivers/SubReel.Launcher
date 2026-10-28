using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Models;
using SubReel.Launcher.Infrastructure.Modpacks;
using Xunit;

namespace SubReel.Launcher.Core.Tests;

public sealed class ModrinthInstanceModServiceTests
{
    [Fact]
    public async Task InstallDownloadsCompatibleModVerifiesHashAndSupportsDisableEnableAndRemove()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var instanceService = new InstanceService(new InMemoryInstanceRepository(), paths, NullLogger<InstanceService>.Instance);
        var instance = await instanceService.CreateAsync("Fabric pack", "1.21.1", MinecraftLoaderType.Fabric, "0.16.9", 4096);
        var payload = Encoding.UTF8.GetBytes("valid-mod-jar-payload");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
        var handler = new ModrinthHandler(payload, sha1);
        var service = new ModrinthInstanceModService(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.modrinth.com/v2/")
        }, instanceService);
        var mod = new InstanceModSearchResult("abc123", "sample-mod", "Sample Mod", "test", null, 1, []);

        await service.InstallAsync(instance, mod);
        var installed = Assert.Single(await service.GetInstalledAsync(instance));
        Assert.Equal("sample-mod.jar", installed.FileName);
        Assert.True(installed.IsEnabled);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(instance.GameDirectory, "mods", "sample-mod.jar")));

        await service.SetEnabledAsync(instance, installed.FileName, enabled: false);
        var disabled = Assert.Single(await service.GetInstalledAsync(instance));
        Assert.False(disabled.IsEnabled);
        Assert.EndsWith(".jar.disabled", disabled.FileName);

        await service.SetEnabledAsync(instance, disabled.FileName, enabled: true);
        var enabled = Assert.Single(await service.GetInstalledAsync(instance));
        Assert.True(enabled.IsEnabled);
        await service.RemoveAsync(instance, enabled.FileName);
        Assert.Empty(await service.GetInstalledAsync(instance));
    }

    [Fact]
    public async Task InstallRejectsHashMismatchAndLeavesNoModFile()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var instanceService = new InstanceService(new InMemoryInstanceRepository(), paths, NullLogger<InstanceService>.Instance);
        var instance = await instanceService.CreateAsync("Fabric pack", "1.21.1", MinecraftLoaderType.Fabric, "0.16.9", 4096);
        var handler = new ModrinthHandler(Encoding.UTF8.GetBytes("bad payload"), "0000000000000000000000000000000000000000");
        var service = new ModrinthInstanceModService(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.modrinth.com/v2/")
        }, instanceService);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(instance,
            new InstanceModSearchResult("abc123", "sample-mod", "Sample Mod", "test", null, 1, [])));
        Assert.False(File.Exists(Path.Combine(instance.GameDirectory, "mods", "sample-mod.jar")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(instance.GameDirectory, "mods")));
    }

    [Fact]
    public async Task SearchAndInstallRejectVanillaInstances()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var instanceService = new InstanceService(new InMemoryInstanceRepository(), paths, NullLogger<InstanceService>.Instance);
        var instance = await instanceService.CreateAsync("Vanilla", "1.21.1", MinecraftLoaderType.Vanilla, null, 4096);
        var service = new ModrinthInstanceModService(new HttpClient(new ModrinthHandler([], ""))
        {
            BaseAddress = new Uri("https://api.modrinth.com/v2/")
        }, instanceService);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync(instance, "sodium"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance,
            new InstanceModSearchResult("abc123", "sample-mod", "Sample Mod", "test", null, 1, [])));
    }

    [Fact]
    public async Task CheckUpdatesFindsModrinthUpdateAndReplacesOnlyTheSelectedMod()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var instanceService = new InstanceService(new InMemoryInstanceRepository(), paths, NullLogger<InstanceService>.Instance);
        var instance = await instanceService.CreateAsync("Fabric pack", "1.21.1", MinecraftLoaderType.Fabric, "0.16.9", 4096);
        var handler = new ModrinthUpdateHandler();
        var service = new ModrinthInstanceModService(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.modrinth.com/v2/")
        }, instanceService);
        var searchResult = new InstanceModSearchResult("abc123", "sample-mod", "Sample Mod", "test", null, 1, []);
        await service.InstallAsync(instance, searchResult);
        handler.UpdateAvailable = true;

        var update = Assert.Single(await service.CheckUpdatesAsync(instance));
        Assert.True(update.HasUpdate);
        Assert.Equal("2.0.0", update.UpdateVersionNumber);

        await service.UpdateAsync(instance, update);

        var installed = Assert.Single(await service.GetInstalledAsync(instance));
        Assert.Equal("sample-mod-2.jar", installed.FileName);
        Assert.Equal(handler.NewPayload, await File.ReadAllBytesAsync(Path.Combine(instance.GameDirectory, "mods", installed.FileName)));
        Assert.False(File.Exists(Path.Combine(instance.GameDirectory, "mods", "sample-mod.jar")));
    }

    [Fact]
    public async Task CurseForgeSearchAndInstallUseAuthenticatedProxyAndVerifySha1()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var instanceService = new InstanceService(new InMemoryInstanceRepository(), paths, NullLogger<InstanceService>.Instance);
        var instance = await instanceService.CreateAsync("Forge pack", "1.20.1", MinecraftLoaderType.Forge, "47.2.0", 4096);
        var payload = Encoding.UTF8.GetBytes("curseforge-valid-jar");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
        var api = new FakeCurseForgeApi(payload, sha1);
        var service = new ModrinthInstanceModService(new HttpClient(new ModrinthHandler([], ""))
        {
            BaseAddress = new Uri("https://api.modrinth.com/v2/")
        }, instanceService, api);
        var results = await service.SearchAsync(instance, "sodium", provider: ModpackProvider.CurseForge);
        var mod = Assert.Single(results);
        Assert.Equal(ModpackProvider.CurseForge, mod.Provider);
        Assert.True(api.SearchMods);

        await service.InstallAsync(instance, mod);

        var installed = Assert.Single(await service.GetInstalledAsync(instance));
        Assert.Equal("sodium-forge.jar", installed.FileName);
        Assert.Equal(ModpackProvider.CurseForge, installed.Provider);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(instance.GameDirectory, "mods", installed.FileName)));
        await service.SetEnabledAsync(instance, installed.FileName, enabled: false);
        var disabled = Assert.Single(await service.GetInstalledAsync(instance));
        Assert.Equal(ModpackProvider.CurseForge, disabled.Provider);
        await service.SetEnabledAsync(instance, disabled.FileName, enabled: true);

        var updatedPayload = Encoding.UTF8.GetBytes("curseforge-updated-jar");
        api.PublishInvalidUpdate(updatedPayload);
        var invalidUpdate = Assert.Single(await service.CheckUpdatesAsync(instance));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.UpdateAsync(instance, invalidUpdate));
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(instance.GameDirectory, "mods", "sodium-forge.jar")));

        api.PublishUpdate(updatedPayload);
        var update = Assert.Single(await service.CheckUpdatesAsync(instance));
        Assert.True(update.HasUpdate);
        Assert.Equal("Sodium Forge 2.0.0", update.UpdateVersionNumber);
        await service.UpdateAsync(instance, update);

        installed = Assert.Single(await service.GetInstalledAsync(instance));
        Assert.Equal("sodium-forge-2.jar", installed.FileName);
        Assert.Equal(updatedPayload, await File.ReadAllBytesAsync(Path.Combine(instance.GameDirectory, "mods", installed.FileName)));
        Assert.False(File.Exists(Path.Combine(instance.GameDirectory, "mods", "sodium-forge.jar")));
    }

    [Fact]
    public async Task CurseForgeInstallResolvesAndInstallsRequiredDependenciesFirst()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var instanceService = new InstanceService(new InMemoryInstanceRepository(), paths, NullLogger<InstanceService>.Instance);
        var instance = await instanceService.CreateAsync("Forge pack", "1.20.1", MinecraftLoaderType.Forge, "47.2.0", 4096);
        var payload = Encoding.UTF8.GetBytes("curseforge-root-mod");
        var api = new FakeCurseForgeApi(payload, Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant());
        api.AddRequiredDependency();
        var service = new ModrinthInstanceModService(new HttpClient(new ModrinthHandler([], ""))
        {
            BaseAddress = new Uri("https://api.modrinth.com/v2/")
        }, instanceService, api);
        var mod = Assert.Single(await service.SearchAsync(instance, "sodium", provider: ModpackProvider.CurseForge));

        await service.InstallAsync(instance, mod);

        var installed = await service.GetInstalledAsync(instance);
        Assert.Equal(2, installed.Count);
        Assert.Contains(installed, item => item.FileName == "library.jar" && item.Provider == ModpackProvider.CurseForge);
        Assert.Contains(installed, item => item.FileName == "sodium-forge.jar" && item.Provider == ModpackProvider.CurseForge);
        Assert.Equal(api.DependencyPayload, await File.ReadAllBytesAsync(Path.Combine(instance.GameDirectory, "mods", "library.jar")));
    }

    [Fact]
    public async Task CurseForgeInstallDoesNotCommitRootModWhenDependencyHashIsInvalid()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestAppPaths(temporary.Path);
        var instanceService = new InstanceService(new InMemoryInstanceRepository(), paths, NullLogger<InstanceService>.Instance);
        var instance = await instanceService.CreateAsync("Forge pack", "1.20.1", MinecraftLoaderType.Forge, "47.2.0", 4096);
        var payload = Encoding.UTF8.GetBytes("curseforge-root-mod");
        var api = new FakeCurseForgeApi(payload, Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant());
        api.AddRequiredDependency(invalidHash: true);
        var service = new ModrinthInstanceModService(new HttpClient(new ModrinthHandler([], ""))
        {
            BaseAddress = new Uri("https://api.modrinth.com/v2/")
        }, instanceService, api);
        var mod = Assert.Single(await service.SearchAsync(instance, "sodium", provider: ModpackProvider.CurseForge));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(instance, mod));

        var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
        Assert.Empty(Directory.EnumerateFiles(modsDirectory, "*.jar"));
        Assert.False(File.Exists(Path.Combine(modsDirectory, ".subreel-curseforge-mods.json")));
    }

    private sealed class FakeCurseForgeApi(byte[] payload, string sha1) : ICurseForgeApiClient
    {
        private byte[] _payload = payload;
        public byte[] DependencyPayload { get; } = Encoding.UTF8.GetBytes("required-library");
        private bool _hasRequiredDependency;
        private bool _invalidDependencyHash;
        public CurseForgeModpackFile LatestFile { get; private set; } = new(456, 123,
            "Sodium Forge 1.0.0", "sodium-forge.jar", null, payload.Length,
            [new CurseForgeFileHash(sha1, 1)], ["1.20.1", "Forge"], true)
        { PublishedAt = DateTimeOffset.UtcNow };
        public bool SearchMods { get; private set; }

        public void AddRequiredDependency(bool invalidHash = false)
        {
            _hasRequiredDependency = true;
            _invalidDependencyHash = invalidHash;
            LatestFile = LatestFile with { Dependencies = [new CurseForgeFileDependency(124, 0, 3)] };
        }

        public void PublishUpdate(byte[] updatedPayload)
        {
            _payload = updatedPayload;
            var updatedSha1 = Convert.ToHexString(SHA1.HashData(updatedPayload)).ToLowerInvariant();
            LatestFile = new CurseForgeModpackFile(789, 123, "Sodium Forge 2.0.0", "sodium-forge-2.jar", null,
                updatedPayload.Length, [new CurseForgeFileHash(updatedSha1, 1)], ["1.20.1", "Forge"], true)
            { PublishedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        }

        public void PublishInvalidUpdate(byte[] updatedPayload)
        {
            _payload = updatedPayload;
            LatestFile = new CurseForgeModpackFile(788, 123, "Broken file", "sodium-broken.jar", null,
                updatedPayload.Length, [new CurseForgeFileHash(new string('0', 40), 1)], ["1.20.1", "Forge"], true)
            { PublishedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        }

        public Task<CurseForgeSearchResults> SearchAsync(ModpackQuery query, CancellationToken cancellationToken = default, bool searchMods = false)
        {
            SearchMods = searchMods;
            var project = new ModpackSummary("123", "sodium", "Sodium", "A mod", null, 100, [], ModpackProvider.CurseForge);
            return Task.FromResult(new CurseForgeSearchResults([project], 1));
        }

        public Task<IReadOnlyList<CurseForgeModpackFile>> GetFilesAsync(int projectId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
        {
            if (projectId == 124)
            {
                var dependencySha1 = Convert.ToHexString(SHA1.HashData(DependencyPayload)).ToLowerInvariant();
                if (_invalidDependencyHash) dependencySha1 = new string('0', 40);
                IReadOnlyList<CurseForgeModpackFile> dependencyFiles = [new CurseForgeModpackFile(987, 124,
                    "Required Library", "library.jar", null, DependencyPayload.Length,
                    [new CurseForgeFileHash(dependencySha1, 1)], ["1.20.1", "Forge"], true)
                { PublishedAt = DateTimeOffset.UtcNow }];
                return Task.FromResult(dependencyFiles);
            }
            if (_hasRequiredDependency)
            {
                var rootSha1 = Convert.ToHexString(SHA1.HashData(_payload)).ToLowerInvariant();
                LatestFile = LatestFile with { Dependencies = [new CurseForgeFileDependency(124, 0, 3)],
                    FileLength = _payload.Length, Hashes = [new CurseForgeFileHash(rootSha1, 1)] };
            }
            IReadOnlyList<CurseForgeModpackFile> files = [LatestFile with { ProjectId = projectId }];
            return Task.FromResult(files);
        }

        public Task<IReadOnlyList<CurseForgeModpackFile>> GetFilesByIdsAsync(IReadOnlyList<int> fileIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeModpackFile>>([]);

        public async Task DownloadFileAsync(int projectId, int fileId, Stream destination, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            var data = fileId == 987 ? DependencyPayload : _payload;
            await destination.WriteAsync(data, cancellationToken);
        }
    }

    private sealed class ModrinthHandler(byte[] payload, string sha1) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path.Contains("/project/abc123/version", StringComparison.Ordinal)
                ? System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        id = "version1", version_type = "release", date_published = "2026-01-01T00:00:00Z",
                        game_versions = new[] { "1.21.1" }, loaders = new[] { "fabric" }, dependencies = Array.Empty<object>(),
                        files = new[] { new { filename = "sample-mod.jar", url = "https://cdn.modrinth.com/test.jar", primary = true,
                            size = payload.Length, hashes = new { sha1 } } }
                    }
                })
                : "{}";
            var response = request.RequestUri.Host == "cdn.modrinth.com"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            return Task.FromResult(response);
        }
    }

    private sealed class ModrinthUpdateHandler : HttpMessageHandler
    {
        public byte[] NewPayload { get; } = Encoding.UTF8.GetBytes("updated-mod-payload");
        private byte[] OldPayload { get; } = Encoding.UTF8.GetBytes("old-mod-payload");
        public bool UpdateAvailable { get; set; }
        private string OldSha1 => Convert.ToHexString(SHA1.HashData(OldPayload)).ToLowerInvariant();
        private string NewSha1 => Convert.ToHexString(SHA1.HashData(NewPayload)).ToLowerInvariant();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "cdn.modrinth.com")
                return JsonOrBytes(request, path.EndsWith("new.jar", StringComparison.Ordinal) ? NewPayload : OldPayload);
            if (path.Contains("/version_file/", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
                return JsonOrBytes(request, Encoding.UTF8.GetBytes(UpdateAvailable
                    ? VersionJson("version2", "2.0.0", "sample-mod-2.jar", "new.jar", NewSha1)
                    : VersionJson("version1", "1.0.0", "sample-mod.jar", "old.jar", OldSha1)));
            if (path.EndsWith("/version/version2", StringComparison.Ordinal))
                return JsonOrBytes(request, Encoding.UTF8.GetBytes(VersionJson("version2", "2.0.0", "sample-mod-2.jar", "new.jar", NewSha1)));
            if (path.EndsWith("/project/abc123/version", StringComparison.Ordinal))
                return JsonOrBytes(request, Encoding.UTF8.GetBytes(UpdateAvailable
                    ? $"[{VersionJson("version2", "2.0.0", "sample-mod-2.jar", "new.jar", NewSha1)}]"
                    : $"[{VersionJson("version1", "1.0.0", "sample-mod.jar", "old.jar", OldSha1)}]"));
            return JsonOrBytes(request, Encoding.UTF8.GetBytes("{}"));
        }

        private static string VersionJson(string id, string number, string fileName, string downloadName, string hash) =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                id,
                project_id = "abc123",
                version_number = number,
                version_type = "release",
                date_published = "2026-01-01T00:00:00Z",
                game_versions = new[] { "1.21.1" },
                loaders = new[] { "fabric" },
                dependencies = Array.Empty<object>(),
                files = new[] { new { filename = fileName, url = $"https://cdn.modrinth.com/{downloadName}", primary = true,
                    size = 100, hashes = new { sha1 = hash } } }
            });

        private static Task<HttpResponseMessage> JsonOrBytes(HttpRequestMessage request, byte[] content) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
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
        public string Shared { get; } = Path.Combine(root, "Shared");
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
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"subreel-mod-tests-{Guid.NewGuid():N}");
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
