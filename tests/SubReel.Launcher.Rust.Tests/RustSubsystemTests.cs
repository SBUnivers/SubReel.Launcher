using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Xunit;
using SubReel.Launcher.Rust.Abstractions.Models;
using SubReel.Launcher.Rust.Abstractions.Services;
using SubReel.Launcher.Rust.Core.Instances;
using SubReel.Launcher.Rust.Infrastructure.Catalog;
using SubReel.Launcher.Rust.Infrastructure.Installers;
using SubReel.Launcher.Rust.Infrastructure.Launching;
using SubReel.Launcher.Rust.Infrastructure.Paths;
using SubReel.Launcher.Rust.Infrastructure.Persistence;

namespace SubReel.Launcher.Rust.Tests;

public sealed class RustSubsystemTests
{
    [Fact]
    public async Task InstanceMetadataSurvivesRepositoryRecreation()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var service = new RustInstanceService(new JsonRustInstanceRepository(paths), paths);

        var created = await service.CreateAsync(PommeReleaseCatalog.ClientId, "  Pomme main  ", "client-v0.2.8+26.2");
        var loaded = await new JsonRustInstanceRepository(paths).GetAllAsync();

        var restored = Assert.Single(loaded);
        Assert.Equal(created.Id, restored.Id);
        Assert.Equal("Pomme main", restored.Name);
        Assert.Equal(PommeReleaseCatalog.ClientId, restored.ClientId);
        Assert.Equal("client-v0.2.8+26.2", restored.ReleaseTag);
    }

    [Fact]
    public async Task InstanceServiceRejectsInvalidNameBeforeWriting()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var service = new RustInstanceService(new JsonRustInstanceRepository(paths), paths);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(PommeReleaseCatalog.ClientId, new string('x', 65), "tag"));
        Assert.False(File.Exists(paths.InstanceIndexFile));
    }

    [Fact]
    public async Task MarkPlayedPersistsLastPlayedTimestampWithoutChangingInstanceIdentity()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var repository = new JsonRustInstanceRepository(paths);
        var service = new RustInstanceService(repository, paths);
        var created = await service.CreateAsync("pomme", "Pomme", "client-v0.2.8+26.2");

        var updated = await service.MarkPlayedAsync(created.Id);
        var restored = Assert.Single(await new JsonRustInstanceRepository(paths).GetAllAsync());

        Assert.NotNull(updated?.LastPlayedAt);
        Assert.Equal(created.Id, restored.Id);
        Assert.Equal(created.Name, restored.Name);
        Assert.Equal(updated!.LastPlayedAt, restored.LastPlayedAt);
    }

    [Fact]
    public async Task RenameInstanceChangesOnlyTheSelectedInstancesName()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var repository = new JsonRustInstanceRepository(paths);
        var service = new RustInstanceService(repository, paths);
        var created = await service.CreateAsync("pomme", "Old name", "client-v0.2.8+26.2");

        var renamed = await service.RenameAsync(created.Id, "  New name  ");

        Assert.Equal("New name", renamed.Name);
        Assert.Equal(created.Id, renamed.Id);
        Assert.Equal(created.ReleaseTag, renamed.ReleaseTag);
        Assert.Equal("New name", Assert.Single(await repository.GetAllAsync()).Name);
    }

    [Fact]
    public async Task DeleteInstanceRemovesOnlyItsRustDataAndMetadata()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var repository = new JsonRustInstanceRepository(paths);
        var service = new RustInstanceService(repository, paths);
        var removed = await service.CreateAsync("pomme", "Removed", "client-v0.2.8+26.2");
        var kept = await service.CreateAsync("pomme", "Kept", "client-v0.2.8+26.2");
        Directory.CreateDirectory(paths.GetInstanceDirectory(removed.Id));
        Directory.CreateDirectory(paths.GetInstanceDirectory(kept.Id));
        await File.WriteAllTextAsync(Path.Combine(paths.GetInstanceDirectory(removed.Id), "world.dat"), "removed world");
        await File.WriteAllTextAsync(Path.Combine(paths.GetInstanceDirectory(kept.Id), "world.dat"), "kept world");
        var releaseDirectory = paths.GetClientReleaseDirectory("pomme", removed.ReleaseTag);
        Directory.CreateDirectory(releaseDirectory);
        await File.WriteAllTextAsync(Path.Combine(releaseDirectory, "pomme-client.exe"), "keep client");

        await service.DeleteAsync(removed.Id);

        var remaining = await repository.GetAllAsync();
        Assert.DoesNotContain(remaining, item => item.Id == removed.Id);
        Assert.Contains(remaining, item => item.Id == kept.Id);
        Assert.False(Directory.Exists(paths.GetInstanceDirectory(removed.Id)));
        Assert.True(File.Exists(Path.Combine(paths.GetInstanceDirectory(kept.Id), "world.dat")));
        Assert.True(File.Exists(Path.Combine(releaseDirectory, "pomme-client.exe")));
    }

    [Fact]
    public async Task RecoverInterruptedDeleteRestoresOrCleansDataAccordingToMetadata()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var repository = new JsonRustInstanceRepository(paths);
        var service = new RustInstanceService(repository, paths);
        var recoverInstance = await service.CreateAsync("pomme", "Recover", "client-v0.2.8+26.2");
        var cleanInstance = await service.CreateAsync("pomme", "Clean", "client-v0.2.8+26.2");
        Directory.CreateDirectory(paths.Instances);
        var restoreTombstone = paths.GetInstanceDirectory(recoverInstance.Id) + ".deleting-" + Guid.NewGuid().ToString("N");
        var cleanTombstone = paths.GetInstanceDirectory(cleanInstance.Id) + ".deleting-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(restoreTombstone);
        Directory.CreateDirectory(cleanTombstone);
        await File.WriteAllTextAsync(Path.Combine(restoreTombstone, "world.dat"), "recover me");
        await File.WriteAllTextAsync(Path.Combine(cleanTombstone, "world.dat"), "delete me");
        await repository.DeleteAsync(cleanInstance.Id);

        var recovered = await service.RecoverInterruptedDeletesAsync();

        Assert.Equal(2, recovered);
        Assert.Equal("recover me", await File.ReadAllTextAsync(Path.Combine(paths.GetInstanceDirectory(recoverInstance.Id), "world.dat")));
        Assert.False(Directory.Exists(cleanTombstone));
    }

    [Fact]
    public async Task PommeCatalogKeepsOfficialWindowsAssetsAndPublishedDigestOnly()
    {
        const string json = """
            [{
              "tag_name":"client-v0.2.8+26.2",
              "html_url":"https://github.com/PommeMC/Client/releases/tag/client-v0.2.8%2B26.2",
              "published_at":"2026-10-25T22:18:01Z",
              "prerelease":false,
              "assets":[
                {"name":"pomme-client-windows-x64-setup.exe","browser_download_url":"https://github.com/PommeMC/Client/releases/download/v1/client.exe","size":123,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},
                {"name":"pomme-client-windows-x64.zip","browser_download_url":"https://example.invalid/client.zip","size":456,"digest":"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},
                {"name":"pomme-client-linux-x64.AppImage","browser_download_url":"https://github.com/PommeMC/Client/releases/download/v1/client.AppImage","size":789,"digest":"sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}
              ]
            }]
            """;
        using var httpClient = new HttpClient(new JsonHandler(json)) { BaseAddress = new Uri("https://api.github.com/") };
        var catalog = new PommeReleaseCatalog(httpClient);

        var release = Assert.Single(await catalog.GetReleasesAsync(PommeReleaseCatalog.ClientId));
        var asset = Assert.Single(release.WindowsX64Assets);
        Assert.Equal("pomme-client-windows-x64-setup.exe", asset.Name);
        Assert.Equal(new string('a', 64), asset.Sha256);
        Assert.Equal("github.com", asset.DownloadUri.Host);
        Assert.Equal("GPL-3.0-or-later", Assert.Single(catalog.GetClients()).License);
    }

    [Fact]
    public async Task PommeCatalogRejectsUnknownClientId()
    {
        using var httpClient = new HttpClient(new JsonHandler("[]")) { BaseAddress = new Uri("https://api.github.com/") };
        var catalog = new PommeReleaseCatalog(httpClient);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.GetReleasesAsync("unknown"));
    }

    [Fact]
    public async Task PommeInstallerVerifiesArchiveAndInstalledFiles()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var archive = CreateZip(("pomme-client.exe", "client-bytes"), ("pomme-core.dll", "library-bytes"));
        var release = CreateRelease(archive);
        using var httpClient = new HttpClient(new BytesHandler(archive));
        var repository = new JsonRustInstallationRepository(paths);
        var installer = new PommeClientInstaller(httpClient, paths, repository);

        var installation = await installer.InstallAsync(release);

        Assert.Equal(release.Tag, installation.ReleaseTag);
        Assert.True(await installer.VerifyAsync("pomme", release.Tag));
        var installDirectory = paths.GetClientReleaseDirectory("pomme", release.Tag);
        Assert.Equal("client-bytes", await File.ReadAllTextAsync(Path.Combine(installDirectory, "pomme-client.exe")));
        await File.WriteAllTextAsync(Path.Combine(installDirectory, "pomme-client.exe"), "tampered");
        Assert.False(await installer.VerifyAsync("pomme", release.Tag));
    }

    [Fact]
    public async Task PommeInstallerRejectsUntrackedFilesBesideVerifiedClient()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var archive = CreateZip(("pomme-client.exe", "client-bytes"), ("pomme-core.dll", "library-bytes"));
        var release = CreateRelease(archive);
        using var httpClient = new HttpClient(new BytesHandler(archive));
        var installer = new PommeClientInstaller(httpClient, paths, new JsonRustInstallationRepository(paths));
        await installer.InstallAsync(release);
        var installDirectory = paths.GetClientReleaseDirectory("pomme", release.Tag);

        await File.WriteAllTextAsync(Path.Combine(installDirectory, "untracked.dll"), "unexpected side-load candidate");

        Assert.False(await installer.VerifyAsync("pomme", release.Tag));
    }

    [Fact]
    public async Task PommeInstallerRejectsHashMismatchWithoutReplacingInstalledFiles()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var archive = CreateZip(("pomme-client.exe", "client-bytes"));
        var release = CreateRelease(archive, new string('0', 64));
        using var httpClient = new HttpClient(new BytesHandler(archive));
        var repository = new JsonRustInstallationRepository(paths);
        var installer = new PommeClientInstaller(httpClient, paths, repository);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(release));

        Assert.False(Directory.Exists(paths.GetClientReleaseDirectory("pomme", release.Tag)));
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task PommeInstallerRejectsArchivePathTraversal()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var archive = CreateZip(("pomme-client.exe", "client-bytes"), ("../outside.txt", "outside"));
        var release = CreateRelease(archive);
        using var httpClient = new HttpClient(new BytesHandler(archive));
        var installer = new PommeClientInstaller(httpClient, paths, new JsonRustInstallationRepository(paths));

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(release));

        Assert.False(File.Exists(Path.Combine(paths.Root, "outside.txt")));
        Assert.False(Directory.Exists(paths.GetClientReleaseDirectory("pomme", release.Tag)));
    }

    [Fact]
    public async Task PommeInstallerHonorsCancellationBeforeNetworkAndFilesystemWork()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var archive = CreateZip(("pomme-client.exe", "client-bytes"));
        var release = CreateRelease(archive);
        using var httpClient = new HttpClient(new BytesHandler(archive));
        var installer = new PommeClientInstaller(httpClient, paths, new JsonRustInstallationRepository(paths));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(release, cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(paths.Root));
    }

    [Fact]
    public async Task PommeInstallerRestoresPreviousFilesWhenStateRegistrationFails()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var repository = new JsonRustInstallationRepository(paths);
        var oldArchive = CreateZip(("pomme-client.exe", "working-old-client"));
        var oldRelease = CreateRelease(oldArchive);
        using (var firstClient = new HttpClient(new BytesHandler(oldArchive)))
            await new PommeClientInstaller(firstClient, paths, repository).InstallAsync(oldRelease);

        var newArchive = CreateZip(("pomme-client.exe", "new-client"));
        using var secondClient = new HttpClient(new BytesHandler(newArchive));
        var failingInstaller = new PommeClientInstaller(secondClient, paths, new FailingInstallationRepository());
        await Assert.ThrowsAsync<IOException>(() => failingInstaller.InstallAsync(CreateRelease(newArchive)));

        var installedPath = Path.Combine(paths.GetClientReleaseDirectory("pomme", oldRelease.Tag), "pomme-client.exe");
        Assert.Equal("working-old-client", await File.ReadAllTextAsync(installedPath));
        Assert.True(await new PommeClientInstaller(secondClient, paths, repository).VerifyAsync("pomme", oldRelease.Tag));
    }

    [Fact]
    public async Task PommeLauncherUsesIsolatedInstanceDirectoriesAndArgumentList()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var instance = new RustInstanceRecord(Guid.NewGuid(), "pomme", "Pomme test", "client-v0.2.8+26.2",
            DateTimeOffset.UtcNow, GameVersion: "26.2");
        var executable = Path.Combine(paths.GetClientReleaseDirectory("pomme", instance.ReleaseTag), "pomme-client.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllTextAsync(executable, "test executable placeholder");
        var processStarter = new CapturingProcessStarter();
        var launcher = new PommeClientLauncher(paths, new VerifiedInstaller(), processStarter);

        var result = await launcher.LaunchAsync(new RustLaunchRequest(instance, "TestPlayer"));

        Assert.Equal(instance.Id, result.InstanceId);
        Assert.Equal(4242, result.ProcessId);
        var startInfo = Assert.IsType<ProcessStartInfo>(processStarter.StartInfo);
        Assert.Equal(executable, startInfo.FileName);
        Assert.Contains("--game-dir", startInfo.ArgumentList);
        Assert.Contains(Path.Combine(paths.GetInstanceDirectory(instance.Id), "game"), startInfo.ArgumentList);
        Assert.Contains(Path.Combine(paths.GetInstanceDirectory(instance.Id), "assets"), startInfo.ArgumentList);
        Assert.Contains(Path.Combine(paths.GetInstanceDirectory(instance.Id), "versions"), startInfo.ArgumentList);
        Assert.Contains("--launch-token", startInfo.ArgumentList);
        Assert.Contains("--version", startInfo.ArgumentList);
        Assert.Contains("26.2", startInfo.ArgumentList);
        Assert.Contains("TestPlayer", startInfo.ArgumentList);
        Assert.True(Directory.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "game")));
    }

    [Fact]
    public async Task PommeLauncherRejectsUnverifiedInstallBeforeStartingProcess()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var instance = new RustInstanceRecord(Guid.NewGuid(), "pomme", "Pomme test", "client-v0.2.8+26.2", DateTimeOffset.UtcNow);
        var processStarter = new CapturingProcessStarter();
        var launcher = new PommeClientLauncher(paths, new UnverifiedInstaller(), processStarter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(new RustLaunchRequest(instance)));

        Assert.Null(processStarter.StartInfo);
        Assert.False(Directory.Exists(paths.Root));
    }

    [Fact]
    public void RustPathsKeepClientAndInstanceDataInsideTheirOwnNamespace()
    {
        using var folder = new TemporaryFolder();
        var paths = new RustPaths(folder.Path);
        var instanceId = Guid.NewGuid();
        var instanceDirectory = paths.GetInstanceDirectory(instanceId);
        var clientDirectory = paths.GetClientReleaseDirectory("pomme", "client-v0.2.8+26.2");

        Assert.Equal(Path.GetFullPath(Path.Combine(folder.Path, "Rust")), paths.Root);
        Assert.StartsWith(Path.Combine(paths.Root, "Instances"), Path.GetFullPath(instanceDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.Combine(paths.Root, "Clients"), Path.GetFullPath(clientDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Minecraft", instanceDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shared", clientDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static RustClientRelease CreateRelease(byte[] archive, string? sha256 = null)
    {
        const string tag = "client-v0.2.8+26.2";
        return new RustClientRelease("pomme", tag, tag, DateTimeOffset.UtcNow, false,
            new Uri("https://github.com/PommeMC/Client/releases/tag/client-v0.2.8%2B26.2"),
            [new RustClientAsset("pomme-client-windows-x64.zip",
                new Uri("https://github.com/PommeMC/Client/releases/download/client-v0.2.8%2B26.2/pomme-client-windows-x64.zip"),
                archive.Length,
                sha256 ?? Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant())]);
    }

    private static byte[] CreateZip(params (string Name, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(file.Content);
            }
        }
        return memory.ToArray();
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
    }

    private sealed class FailingInstallationRepository : IRustInstallationRepository
    {
        public Task<IReadOnlyList<RustInstalledClient>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RustInstalledClient>>([]);
        public Task<RustInstalledClient?> GetAsync(string clientId, string releaseTag, CancellationToken cancellationToken = default) =>
            Task.FromResult<RustInstalledClient?>(null);
        public Task SaveAsync(RustInstalledClient installation, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Simulated index failure"));
        public Task DeleteAsync(string clientId, string releaseTag, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class VerifiedInstaller : IRustClientInstaller
    {
        public Task<RustInstalledClient> InstallAsync(RustClientRelease release, IProgress<RustInstallProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> VerifyAsync(string clientId, string releaseTag, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class UnverifiedInstaller : IRustClientInstaller
    {
        public Task<RustInstalledClient> InstallAsync(RustClientRelease release, IProgress<RustInstallProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> VerifyAsync(string clientId, string releaseTag, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class CapturingProcessStarter : IRustProcessStarter
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public int Start(ProcessStartInfo startInfo) { StartInfo = startInfo; return 4242; }
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SubReelRustTests", Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

}
