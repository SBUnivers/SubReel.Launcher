using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Models;

namespace SubReel.Launcher.Infrastructure.Modpacks;

/// <summary>Imports a CurseForge manifest archive, resolves mod file IDs through the SubReel proxy and installs overrides.</summary>
public sealed class CurseForgeModpackInstaller(ICurseForgeApiClient api, IAppPaths paths, InstanceService instances) : ICurseForgeModpackInstaller
{
    private const long MaxPackArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxExtractedBytes = 8L * 1024 * 1024 * 1024;
    private const int MaxArchiveEntries = 100_000;

    public async Task<InstalledModpack> InstallAsync(ModpackSummary project, ModpackVersionSummary version, int memoryMb = 4096,
        IProgress<ModpackInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(project.Id, out var projectId) || !int.TryParse(version.Id, out var packFileId) ||
            !(version.FileName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ?? false))
            throw new InvalidDataException("Некорректная версия модпака CurseForge.");

        paths.EnsureCreated();
        var staging = Path.Combine(paths.Cache, "curseforge-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var packPath = Path.Combine(staging, "pack.zip");
        MinecraftInstance? instance = null;
        try
        {
            progress?.Report(new("Загружаю архив CurseForge…", 0.03));
            await using (var output = new FileStream(packPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                await api.DownloadFileAsync(projectId, packFileId, output, cancellationToken: cancellationToken);
                if (output.Length is <= 0 or > MaxPackArchiveBytes) throw new InvalidDataException("Размер архива модпака недопустим.");
            }

            var manifest = ReadManifest(packPath);
            var loader = ParseLoader(manifest.MinecraftLoaders);
            var packName = string.IsNullOrWhiteSpace(manifest.Name) ? project.Title : manifest.Name.Trim();
            instance = await instances.CreateAsync(packName, manifest.MinecraftVersion, loader.Type, loader.Version, memoryMb, cancellationToken);
            instance.Source = $"CurseForge:{projectId}:{packFileId}";
            await instances.UpdateAsync(instance, cancellationToken);

            var missingFiles = new List<string>();
            var fileEntries = manifest.Files.Where(item => item.Required).ToArray();
            for (var offset = 0; offset < fileEntries.Length; offset += 50)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = fileEntries.Skip(offset).Take(50).ToArray();
                var metadata = await api.GetFilesByIdsAsync(batch.Select(item => item.FileId).ToArray(), cancellationToken);
                var filesById = metadata.ToDictionary(item => item.Id);
                foreach (var item in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = filesById.GetValueOrDefault(item.FileId);
                    if (target is null || !target.IsAvailable || target.ProjectId != item.ProjectId)
                    {
                        missingFiles.Add(item.FileId.ToString());
                        continue;
                    }
                    var fileName = Path.GetFileName(target.FileName);
                    if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
                        throw new InvalidDataException($"У файла CurseForge {target.Id} некорректное имя.");
                    if (target.FileLength is < 0 or > MaxPackArchiveBytes)
                        throw new InvalidDataException($"Размер файла {fileName} превышает допустимый лимит.");
                    var modDirectory = Path.Combine(instance.GameDirectory, "mods");
                    Directory.CreateDirectory(modDirectory);
                    var destination = Path.Combine(modDirectory, fileName);
                    try
                    {
                        progress?.Report(new($"Скачиваю мод {offset + Array.IndexOf(batch, item) + 1} из {fileEntries.Length}: {fileName}",
                            0.1 + 0.78 * (offset + Array.IndexOf(batch, item)) / Math.Max(1, fileEntries.Length)));
                        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                            await api.DownloadFileAsync(item.ProjectId, item.FileId, output, cancellationToken: cancellationToken);
                        if (!await HasValidHashAsync(destination, target.Hashes, cancellationToken))
                            throw new InvalidDataException($"Не совпала контрольная сумма файла {fileName}.");
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
                    {
                        if (File.Exists(destination)) File.Delete(destination);
                        missingFiles.Add(fileName);
                    }
                }
            }

            await ExtractOverridesAsync(packPath, instance.GameDirectory, manifest.OverridesDirectory, progress, cancellationToken);
            var warning = missingFiles.Count == 0 ? null : $"Не удалось получить {missingFiles.Count} файл(ов) из CurseForge: {string.Join(", ", missingFiles.Take(8))}";
            progress?.Report(new("Профиль CurseForge создан.", 1));
            return new InstalledModpack(instance.Id, instance.Name, instance.MinecraftVersion, instance.LoaderType.ToString(), warning);
        }
        catch
        {
            if (instance is not null)
            {
                try
                {
                    await instances.DeleteAsync(instance.Id, CancellationToken.None);
                    var instanceRoot = Path.GetDirectoryName(instance.GameDirectory);
                    if (instanceRoot is not null && Directory.Exists(instanceRoot)) Directory.Delete(instanceRoot, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static PackManifest ReadManifest(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("В ZIP нет manifest.json CurseForge.");
        using var stream = manifestEntry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("manifestType", out var kind) || kind.GetString() != "minecraftModpack")
            throw new InvalidDataException("Архив не является сборкой Minecraft из CurseForge.");
        var minecraft = root.GetProperty("minecraft");
        var gameVersion = minecraft.GetProperty("version").GetString();
        if (string.IsNullOrWhiteSpace(gameVersion)) throw new InvalidDataException("В манифесте не указана версия Minecraft.");
        var loaders = minecraft.GetProperty("modLoaders").EnumerateArray()
            .Where(x => !x.TryGetProperty("primary", out var primary) || primary.ValueKind == JsonValueKind.True)
            .Select(x => x.GetProperty("id").GetString() ?? "").Where(x => x.Length > 0).ToArray();
        var files = root.GetProperty("files").EnumerateArray().Select(x => new PackFile(
            x.GetProperty("projectID").GetInt32(), x.GetProperty("fileID").GetInt32(),
            !x.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.False)).ToArray();
        return new(root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            gameVersion, loaders, files, root.TryGetProperty("overrides", out var overrides) ? overrides.GetString() ?? "overrides" : "overrides");
    }

    private static (MinecraftLoaderType Type, string? Version) ParseLoader(IReadOnlyList<string> loaders)
    {
        foreach (var id in loaders)
        {
            var separator = id.IndexOf('-');
            var name = separator < 0 ? id : id[..separator];
            var version = separator < 0 ? null : id[(separator + 1)..];
            if (Enum.TryParse<MinecraftLoaderType>(name, true, out var type) && Enum.IsDefined(type)) return (type, version);
        }
        return (MinecraftLoaderType.Vanilla, null);
    }

    private static async Task ExtractOverridesAsync(string zipPath, string gameDirectory, string overridesFolder,
        IProgress<ModpackInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(overridesFolder) || overridesFolder.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(overridesFolder))
            throw new InvalidDataException("В манифесте указан небезопасный путь overrides.");
        using var archive = ZipFile.OpenRead(zipPath);
        var prefix = overridesFolder.TrimEnd('/', '\\').Replace('\\', '/') + "/";
        var root = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long totalBytes = 0;
        var entries = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entries > MaxArchiveEntries) throw new InvalidDataException("В архиве слишком много записей.");
            var normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = normalized[prefix.Length..];
            if (relative.Length == 0) continue;
            var destination = Path.GetFullPath(Path.Combine(gameDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Обнаружен небезопасный путь в overrides.");
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000) throw new InvalidDataException("Символические ссылки в overrides запрещены.");
            if (normalized.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
            if (entry.Length > MaxExtractedBytes || entry.Length > MaxPackArchiveBytes) throw new InvalidDataException("Распакованный архив превышает допустимый размер.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var buffer = new byte[128 * 1024];
            long copied = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                copied = checked(copied + read);
                totalBytes = checked(totalBytes + read);
                if (copied > entry.Length || totalBytes > MaxExtractedBytes)
                    throw new InvalidDataException("Фактический размер распаковки превышает допустимый предел.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (copied != entry.Length) throw new InvalidDataException("Размер файла overrides не совпадает с архивом.");
        }
        progress?.Report(new("Файлы overrides установлены.", 0.98));
    }

    private static async Task<bool> HasValidHashAsync(string path, IReadOnlyList<CurseForgeFileHash> hashes, CancellationToken ct)
    {
        var sha1 = hashes.FirstOrDefault(hash => hash.Algorithm == 1)?.Value;
        if (string.IsNullOrWhiteSpace(sha1)) return true;
        await using var stream = File.OpenRead(path);
        var actual = await SHA1.HashDataAsync(stream, ct);
        return Convert.ToHexString(actual).Equals(sha1, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PackManifest(string Name, string MinecraftVersion, IReadOnlyList<string> MinecraftLoaders,
        IReadOnlyList<PackFile> Files, string OverridesDirectory);
    private sealed record PackFile(int ProjectId, int FileId, bool Required);
}
