using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Models;

namespace SubReel.Launcher.Infrastructure.Modpacks;

/// <summary>Installs client files and overrides from a Modrinth .mrpack archive.</summary>
public sealed class ModrinthModpackInstaller(HttpClient httpClient, IAppPaths paths, InstanceService instances) : IModpackInstaller
{
    private const int MaxArchiveEntries = 50_000;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;

    public async Task<InstalledModpack> InstallAsync(
        ModpackSummary project,
        ModpackVersionSummary version,
        int memoryMb = 4096,
        IProgress<ModpackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(version);
        if (string.IsNullOrWhiteSpace(version.DownloadUrl) || string.IsNullOrWhiteSpace(version.FileName))
            throw new InvalidOperationException("Для этой версии нет доступного файла .mrpack.");
        if (!Uri.TryCreate(version.DownloadUrl, UriKind.Absolute, out var archiveUri) || archiveUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Modrinth вернул небезопасный адрес файла.");

        paths.EnsureCreated();
        var staging = Path.Combine(paths.Cache, "modrinth-install", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, "pack.mrpack");
        MinecraftInstance? createdInstance = null;
        try
        {
            progress?.Report(new ModpackInstallProgress("Скачиваю архив Modrinth…", 0.05));
            await DownloadFileAsync(archiveUri, archivePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(version.Sha1))
                await VerifySha1Async(archivePath, version.Sha1, cancellationToken);

            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > MaxArchiveEntries) throw new InvalidDataException("В архиве слишком много файлов.");
            var indexEntry = archive.GetEntry("modrinth.index.json")
                ?? throw new InvalidDataException("В архиве отсутствует modrinth.index.json.");
            await using var indexStream = indexEntry.Open();
            using var index = await JsonDocument.ParseAsync(indexStream, cancellationToken: cancellationToken);
            var root = index.RootElement;
            var dependencies = root.GetProperty("dependencies");
            var minecraftVersion = ReadRequiredString(dependencies, "minecraft");
            var (loader, loaderVersion) = ResolveLoader(dependencies);
            var name = ReadOptionalString(root, "name") ?? project.Title;

            var files = ReadClientFiles(root);
            var overrides = archive.Entries.Where(entry =>
                entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.FullName.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToArray();
            if (files.Count + overrides.Length > MaxArchiveEntries) throw new InvalidDataException("Сборка содержит слишком много файлов.");

            createdInstance = await instances.CreateAsync(name, minecraftVersion, loader, loaderVersion, memoryMb, cancellationToken);
            createdInstance.Source = $"Modrinth:{project.Id}:{version.Id}";
            await instances.UpdateAsync(createdInstance, cancellationToken);

            long totalBytes = files.Sum(file => file.Size) + overrides.Sum(entry => entry.Length);
            if (totalBytes > MaxExpandedBytes) throw new InvalidDataException("Размер распакованной сборки превышает 2 ГБ.");
            var completed = 0;
            using var parallel = new SemaphoreSlim(4, 4);
            var downloads = files.Select(async file =>
            {
                await parallel.WaitAsync(cancellationToken);
                try
                {
                    var destination = GetSafeDestination(createdInstance.GameDirectory, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await DownloadFileAsync(file.Url, destination, cancellationToken);
                    await VerifySha1Async(destination, file.Sha1, cancellationToken);
                    var index = Interlocked.Increment(ref completed);
                    progress?.Report(new ModpackInstallProgress($"Загружено файлов: {index}/{files.Count}", 0.1 + 0.75 * index / Math.Max(1, files.Count + overrides.Length)));
                }
                finally { parallel.Release(); }
            }).ToArray();
            await Task.WhenAll(downloads);

            foreach (var entry in overrides)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = entry.FullName.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase)
                    ? entry.FullName["client-overrides/".Length..]
                    : entry.FullName["overrides/".Length..];
                if (string.IsNullOrEmpty(relative)) continue;
                var destination = GetSafeDestination(createdInstance.GameDirectory, relative);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException($"Символические ссылки в архиве запрещены: {entry.FullName}");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await input.CopyToAsync(output, cancellationToken);
                var count = Interlocked.Increment(ref completed);
                progress?.Report(new ModpackInstallProgress($"Распаковка файлов: {count}/{files.Count + overrides.Length}", 0.1 + 0.75 * count / Math.Max(1, files.Count + overrides.Length)));
            }

            progress?.Report(new ModpackInstallProgress("Сборка установлена. Minecraft подготовится при первом запуске.", 1));
            return new InstalledModpack(createdInstance.Id, createdInstance.Name, minecraftVersion, loader.ToString());
        }
        catch
        {
            if (createdInstance is not null)
            {
                try { await instances.DeleteAsync(createdInstance.Id, CancellationToken.None); } catch { }
                var instanceRoot = Path.GetDirectoryName(createdInstance.GameDirectory);
                var instancesRoot = Path.GetFullPath(paths.Instances) + Path.DirectorySeparatorChar;
                if (instanceRoot is not null && Path.GetFullPath(instanceRoot).StartsWith(instancesRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(instanceRoot))
                {
                    try { Directory.Delete(instanceRoot, recursive: true); } catch { }
                }
            }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static (MinecraftLoaderType Type, string? Version) ResolveLoader(JsonElement dependencies)
    {
        foreach (var (key, type) in new[]
        {
            ("fabric-loader", MinecraftLoaderType.Fabric),
            ("quilt-loader", MinecraftLoaderType.Quilt),
            ("neoforge", MinecraftLoaderType.NeoForge),
            ("forge", MinecraftLoaderType.Forge)
        })
        {
            var value = ReadOptionalString(dependencies, key);
            if (!string.IsNullOrWhiteSpace(value)) return (type, value);
        }
        return (MinecraftLoaderType.Vanilla, null);
    }

    private static List<PackFile> ReadClientFiles(JsonElement root)
    {
        var result = new List<PackFile>();
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return result;
        foreach (var file in files.EnumerateArray())
        {
            if (file.TryGetProperty("env", out var env) && env.TryGetProperty("client", out var client) &&
                string.Equals(client.GetString(), "unsupported", StringComparison.OrdinalIgnoreCase)) continue;
            var path = ReadRequiredString(file, "path");
            var hashes = file.GetProperty("hashes");
            var hash = ReadRequiredString(hashes, "sha1");
            var urls = file.GetProperty("downloads");
            var url = urls.EnumerateArray().Select(element => Uri.TryCreate(element.GetString(), UriKind.Absolute, out var uri) ? uri : null)
                .FirstOrDefault(uri => uri?.Scheme == Uri.UriSchemeHttps)
                ?? throw new InvalidDataException($"Нет HTTPS-ссылки для файла сборки: {path}");
            var size = file.TryGetProperty("fileSize", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
            result.Add(new PackFile(path, url, hash, Math.Max(0, size)));
        }
        return result;
    }

    private async Task DownloadFileAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static async Task VerifySha1Async(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Хеш SHA1 не совпал: {Path.GetFileName(path)}");
    }

    private static string GetSafeDestination(string rootDirectory, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part == ".."))
            throw new InvalidDataException($"Недопустимый путь в сборке: {relativePath}");
        var root = Path.GetFullPath(rootDirectory) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(rootDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Путь выходит за пределы папки сборки: {relativePath}");
        return destination;
    }

    private static string ReadRequiredString(JsonElement element, string name) =>
        ReadOptionalString(element, name) ?? throw new InvalidDataException($"В индексе сборки отсутствует поле {name}.");

    private static string? ReadOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record PackFile(string Path, Uri Url, string Sha1, long Size);
}
