using SubReel.Launcher.Core.Models;
using SubReel.Launcher.Abstractions.Paths;
using Microsoft.Extensions.Logging;

namespace SubReel.Launcher.Core.Instances;

public sealed class InstanceService(IInstanceRepository repository, IAppPaths paths, ILogger<InstanceService> logger)
{
    public string GetValidatedGameDirectory(MinecraftInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Id == Guid.Empty)
            throw new InvalidOperationException("У сборки отсутствует корректный идентификатор.");

        var instancesRoot = Path.GetFullPath(paths.Instances);
        if (Directory.Exists(instancesRoot) && IsReparsePoint(instancesRoot))
            throw new InvalidOperationException("Корневой каталог инстансов является ссылкой.");

        var instanceRoot = Path.GetFullPath(Path.Combine(instancesRoot, instance.Id.ToString("N")));
        if (!string.Equals(Path.GetRelativePath(instancesRoot, instanceRoot), instance.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь сборки не прошёл проверку безопасности.");
        if (Directory.Exists(instanceRoot) && IsReparsePoint(instanceRoot))
            throw new InvalidOperationException("Каталог сборки является ссылкой.");

        var gameDirectory = Path.GetFullPath(instance.GameDirectory);
        var relative = Path.GetRelativePath(instanceRoot, gameDirectory);
        if (Path.IsPathRooted(relative) || relative == "." || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Папка игры находится за пределами каталога этой сборки.");
        if (Directory.Exists(gameDirectory) && IsReparsePoint(gameDirectory))
            throw new InvalidOperationException("Игровая папка является ссылкой.");

        return gameDirectory;
    }

    public Task<IReadOnlyList<MinecraftInstance>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return repository.GetAllAsync(cancellationToken);
    }

    public async Task<MinecraftInstance> CreateAsync(
        string name,
        string minecraftVersion,
        MinecraftLoaderType loaderType,
        string? loaderVersion,
        int memoryMb,
        CancellationToken cancellationToken = default,
        bool isModpackInstall = false)
    {
        name = name.Trim();
        minecraftVersion = minecraftVersion.Trim();

        if (name.Length is < 1 or > 60)
            throw new ArgumentException(
                "Название сборки должно содержать от 1 до 60 символов.",
                nameof(name));

        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException(
                "Укажи версию Minecraft.",
                nameof(minecraftVersion));

        if (minecraftVersion.Length > 32)
            throw new ArgumentException(
                "Слишком длинная версия Minecraft.",
                nameof(minecraftVersion));

        if (!Enum.IsDefined(loaderType))
            throw new ArgumentOutOfRangeException(nameof(loaderType));

        if (memoryMb is < 512 or > 65536)
            throw new ArgumentOutOfRangeException(
                nameof(memoryMb),
                "Память должна быть в диапазоне 512–65536 МБ.");

        if (loaderType == MinecraftLoaderType.Vanilla)
            loaderVersion = null;
        else if (string.IsNullOrWhiteSpace(loaderVersion))
            loaderVersion = null;
        else
            loaderVersion = loaderVersion.Trim();

        paths.EnsureCreated();
        var id = Guid.NewGuid();
        var instanceRoot = Path.Combine(paths.Instances, id.ToString("N"));
        var instance = new MinecraftInstance
        {
            Id = id,
            Name = name,
            MinecraftVersion = minecraftVersion,
            LoaderType = loaderType,
            LoaderVersion = loaderVersion,
            MemoryMb = memoryMb,
            InstallState = isModpackInstall ? InstanceInstallState.Installing : InstanceInstallState.Ready,
            GameDirectory = Path.Combine(instanceRoot, "game")
        };

        Directory.CreateDirectory(instance.GameDirectory);

        try
        {
            await repository.SaveAsync(instance, cancellationToken);
            return instance;
        }
        catch
        {
            // Удаляем только созданную нами пустую папку.
            if (Directory.Exists(instance.GameDirectory) && !Directory.EnumerateFileSystemEntries(instance.GameDirectory).Any())
                Directory.Delete(instance.GameDirectory);
            if (Directory.Exists(instanceRoot) && !Directory.EnumerateFileSystemEntries(instanceRoot).Any())
                Directory.Delete(instanceRoot);

            throw;
        }
    }

    /// <summary>Removes modpack instances left in an incomplete install state after an unclean process exit.</summary>
    public async Task<int> RecoverInterruptedModpackInstallsAsync(CancellationToken cancellationToken = default)
    {
        CleanupInterruptedInstallStaging(cancellationToken);
        await CleanupInterruptedInstanceDeletionsAsync(cancellationToken);
        var interrupted = (await repository.GetAllAsync(cancellationToken))
            .Where(instance => instance.InstallState == InstanceInstallState.Installing)
            .ToArray();
        var recovered = 0;
        foreach (var instance in interrupted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteAsync(instance.Id, deleteFiles: true, cancellationToken);
            recovered++;
        }
        return recovered;
    }

    private async Task CleanupInterruptedInstanceDeletionsAsync(CancellationToken cancellationToken)
    {
        var instancesRoot = Path.GetFullPath(paths.Instances);
        if (!Directory.Exists(instancesRoot) || IsReparsePoint(instancesRoot)) return;
        var existingInstances = (await repository.GetAllAsync(cancellationToken))
            .ToDictionary(instance => instance.Id);

        foreach (var directory in Directory.EnumerateDirectories(instancesRoot, ".deleting-*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            const string prefix = ".deleting-";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || IsReparsePoint(directory))
                continue;
            var identity = name[prefix.Length..];
            var separator = identity.IndexOf('-');
            if (separator <= 0 ||
                !Guid.TryParseExact(identity[..separator], "N", out var instanceId) ||
                !Guid.TryParseExact(identity[(separator + 1)..], "N", out _))
                continue;
            if (existingInstances.TryGetValue(instanceId, out var existingInstance))
            {
                var originalDirectory = Path.GetFullPath(Path.Combine(instancesRoot, instanceId.ToString("N")));
                if (!Directory.Exists(originalDirectory) && !File.Exists(originalDirectory) &&
                    IsDirectChild(instancesRoot, directory) && IsDirectChild(instancesRoot, originalDirectory))
                {
                    try
                    {
                        Directory.Move(directory, originalDirectory);
                        logger.LogWarning("Restored staged files for instance {InstanceId} because its catalog record still exists", instanceId);
                        continue;
                    }
                    catch (IOException ex)
                    {
                        logger.LogError(ex, "Could not restore staged files for instance {InstanceId}; preserving them at {Directory}", instanceId, directory);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        logger.LogError(ex, "Could not restore staged files for instance {InstanceId}; preserving them at {Directory}", instanceId, directory);
                    }
                }

                logger.LogError("Preserving staged files for instance {InstanceId} because its catalog record still exists: {Directory}", existingInstance.Id, directory);
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                logger.LogInformation("Removed instance deletion staging directory {Directory}", directory);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not clean interrupted instance deletion directory {Directory}", directory);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Could not clean interrupted instance deletion directory {Directory}", directory);
            }
        }
    }

    private void CleanupInterruptedInstallStaging(CancellationToken cancellationToken)
    {
        var cacheRoot = Path.GetFullPath(paths.Cache);
        if (!Directory.Exists(cacheRoot) || IsReparsePoint(cacheRoot)) return;

        var modrinthRoot = Path.Combine(cacheRoot, "modrinth-install");
        if (Directory.Exists(modrinthRoot) && !IsReparsePoint(modrinthRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(modrinthRoot, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                    TryDeleteStagingDirectory(modrinthRoot, directory);
            }
            TryDeleteEmptyStagingRoot(cacheRoot, modrinthRoot);
        }

        foreach (var directory in Directory.EnumerateDirectories(cacheRoot, "curseforge-import-*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            const string prefix = "curseforge-import-";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(name[prefix.Length..], "N", out _))
                TryDeleteStagingDirectory(cacheRoot, directory);
        }
    }

    private static void TryDeleteEmptyStagingRoot(string cacheRoot, string stagingRoot)
    {
        try
        {
            if (IsDirectChild(cacheRoot, stagingRoot) && !IsReparsePoint(stagingRoot) &&
                !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
                Directory.Delete(stagingRoot);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteStagingDirectory(string cacheRoot, string directory)
    {
        try
        {
            if (IsDirectChild(cacheRoot, directory) && Directory.Exists(directory) && !IsReparsePoint(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsDirectChild(string parent, string candidate)
    {
        var fullParent = Path.GetFullPath(parent);
        var fullCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(fullParent, fullCandidate);
        return !Path.IsPathRooted(relative) && relative != "." &&
            !relative.Contains(Path.DirectorySeparatorChar) && !relative.Contains(Path.AltDirectorySeparatorChar) &&
            !relative.StartsWith("..", StringComparison.Ordinal);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public async Task UpdateAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var existing = await repository.GetByIdAsync(
            instance.Id,
            cancellationToken);

        if (existing is null)
            throw new InvalidOperationException(
                "Сборка не найдена.");

        // The game directory is part of the instance identity. Editing settings
        // must never let a caller redirect a manifest or game files elsewhere.
        var existingDirectory = GetValidatedGameDirectory(existing);
        var updatedDirectory = GetValidatedGameDirectory(instance);
        if (!string.Equals(existingDirectory, updatedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Нельзя изменить игровую папку существующей сборки.");

        if (string.IsNullOrWhiteSpace(instance.Name) ||
            instance.Name.Trim().Length > 60)
            throw new ArgumentException(
                "Название должно содержать от 1 до 60 символов.");

        if (string.IsNullOrWhiteSpace(instance.MinecraftVersion) ||
            instance.MinecraftVersion.Trim().Length > 32)
            throw new ArgumentException(
                "Укажи корректную версию Minecraft.");

        if (instance.MemoryMb is < 512 or > 65536)
            throw new ArgumentOutOfRangeException(
                nameof(instance.MemoryMb));

        if (!Enum.IsDefined(instance.LoaderType))
            throw new ArgumentOutOfRangeException(
                nameof(instance.LoaderType));

        if (instance.LoaderVersion?.Length > 64)
            throw new ArgumentException("Слишком длинная версия загрузчика.", nameof(instance));

        instance.Name = instance.Name.Trim();
        instance.MinecraftVersion = instance.MinecraftVersion.Trim();

        if (instance.LoaderType == MinecraftLoaderType.Vanilla)
            instance.LoaderVersion = null;
        else if (string.IsNullOrWhiteSpace(instance.LoaderVersion))
            instance.LoaderVersion = null;
        else
            instance.LoaderVersion = instance.LoaderVersion.Trim();

        await repository.SaveAsync(instance, cancellationToken);
    }

    public async Task<MinecraftInstance> DuplicateAsync(
        MinecraftInstance source,
        InstanceCopyOptions? copyOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        copyOptions ??= new InstanceCopyOptions();
        var sourceGameDirectory = GetValidatedGameDirectory(source);
        var suffix = " (копия)";
        var baseName = source.Name.Length + suffix.Length <= 60
            ? source.Name
            : source.Name[..(60 - suffix.Length)];
        var duplicate = await CreateAsync(
            baseName + suffix,
            source.MinecraftVersion,
            source.LoaderType,
            source.LoaderVersion,
            source.MemoryMb,
            cancellationToken);

        try
        {
            duplicate.IconPath = source.IconPath;
            duplicate.JavaPath = source.JavaPath;
            duplicate.JvmArguments = source.JvmArguments;
            duplicate.Source = "Manual";
            await CopySelectedInstanceDataAsync(sourceGameDirectory, duplicate.GameDirectory, copyOptions, cancellationToken);
            await UpdateAsync(duplicate, cancellationToken);
            return duplicate;
        }
        catch
        {
            try { await repository.DeleteAsync(duplicate.Id, CancellationToken.None); }
            catch (Exception cleanupException)
            {
                logger.LogError(cleanupException, "Could not remove the duplicate instance record after copy failed for {InstanceId}", duplicate.Id);
            }

            TryDeleteCreatedDuplicateFiles(duplicate);
            throw;
        }
    }

    private async Task CopySelectedInstanceDataAsync(
        string sourceGameDirectory,
        string targetGameDirectory,
        InstanceCopyOptions options,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceGameDirectory)) return;

        if (options.Saves) await CopyDirectoryAsync("saves");
        if (options.ResourcePacks) await CopyDirectoryAsync("resourcepacks");
        if (options.ShaderPacks) await CopyDirectoryAsync("shaderpacks");
        if (options.Screenshots) await CopyDirectoryAsync("screenshots");
        if (options.Mods) await CopyDirectoryAsync("mods");
        if (options.GameOptions) await CopyFileAsync("options.txt");
        if (options.Servers) await CopyFileAsync("servers.dat");

        async Task CopyDirectoryAsync(string relativePath)
        {
            var source = Path.Combine(sourceGameDirectory, relativePath);
            if (!Directory.Exists(source)) return;
            if (IsReparsePoint(source))
                throw new InvalidOperationException($"Нельзя копировать ссылку из игровой папки: {relativePath}.");

            var target = Path.Combine(targetGameDirectory, relativePath);
            await CopyDirectoryContentsAsync(source, target, cancellationToken);
        }

        async Task CopyFileAsync(string relativePath)
        {
            var source = Path.Combine(sourceGameDirectory, relativePath);
            if (!File.Exists(source)) return;
            if (IsReparsePoint(source))
                throw new InvalidOperationException($"Нельзя копировать ссылку из игровой папки: {relativePath}.");

            var target = Path.Combine(targetGameDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await CopyFileContentsAsync(source, target, cancellationToken);
        }
    }

    private static async Task CopyDirectoryContentsAsync(string source, string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsReparsePoint(source))
            throw new InvalidOperationException($"Нельзя копировать ссылку из игровой папки: {source}.");
        Directory.CreateDirectory(target);

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(entry))
                throw new InvalidOperationException($"Нельзя копировать ссылку из игровой папки: {entry}.");

            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry))
                await CopyDirectoryContentsAsync(entry, destination, cancellationToken);
            else
                await CopyFileContentsAsync(entry, destination, cancellationToken);
        }
    }

    private static async Task CopyFileContentsAsync(string source, string target, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 64 * 1024, cancellationToken);
        await output.FlushAsync(cancellationToken);
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
    }

    private void TryDeleteCreatedDuplicateFiles(MinecraftInstance duplicate)
    {
        try
        {
            var instancesRoot = Path.GetFullPath(paths.Instances);
            var instanceRoot = Path.GetFullPath(Path.Combine(instancesRoot, duplicate.Id.ToString("N")));
            if (IsDirectChild(instancesRoot, instanceRoot) && Directory.Exists(instanceRoot) && !ContainsReparsePoint(instanceRoot))
                Directory.Delete(instanceRoot, recursive: true);
        }
        catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(cleanupException, "Could not clean files for failed duplicate instance {InstanceId}", duplicate.Id);
        }
    }

    private static bool ContainsReparsePoint(string path)
    {
        if (IsReparsePoint(path)) return true;
        if (!Directory.Exists(path)) return false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (IsReparsePoint(entry)) return true;
            if (Directory.Exists(entry) && ContainsReparsePoint(entry)) return true;
        }
        return false;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        DeleteAsync(id, deleteFiles: false, cancellationToken);

    public async Task DeleteAsync(
        Guid id,
        bool deleteFiles = false,
        CancellationToken cancellationToken = default)
    {
        var instance = await repository.GetByIdAsync(
            id,
            cancellationToken);

        if (instance is null)
            return;

        var stagedDirectory = deleteFiles ? StageInstanceFilesForDeletion(instance) : null;
        try
        {
            await repository.DeleteAsync(id, cancellationToken);
        }
        catch (Exception deleteException)
        {
            if (stagedDirectory is not null && Directory.Exists(stagedDirectory))
            {
                try
                {
                    var originalDirectory = Path.Combine(Path.GetFullPath(paths.Instances), instance.Id.ToString("N"));
                    Directory.Move(stagedDirectory, originalDirectory);
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"Не удалось удалить запись сборки и восстановить её файлы. Файлы сохранены во временной папке: {stagedDirectory}",
                        new AggregateException(deleteException, rollbackException));
                }
            }

            throw;
        }

        if (stagedDirectory is not null && Directory.Exists(stagedDirectory))
        {
            try
            {
                Directory.Delete(stagedDirectory, recursive: true);
            }
            catch (IOException ex)
            {
                // The catalog record is already committed; retry cleanup on the next startup.
                logger.LogWarning(ex, "Instance {InstanceId} was removed but its staged files could not yet be deleted", id);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Instance {InstanceId} was removed but its staged files could not yet be deleted", id);
            }
        }
    }

    private string? StageInstanceFilesForDeletion(MinecraftInstance instance)
    {
        var instancesRoot = Path.GetFullPath(paths.Instances);
        var gameDirectory = GetValidatedGameDirectory(instance);
        var instanceRoot = Path.GetFullPath(Path.Combine(instancesRoot, instance.Id.ToString("N")));

        if (!Directory.Exists(instanceRoot)) return null;

        var stagedDirectory = Path.Combine(instancesRoot, $".deleting-{instance.Id:N}-{Guid.NewGuid():N}");
        Directory.Move(instanceRoot, stagedDirectory);
        return stagedDirectory;
    }
}
