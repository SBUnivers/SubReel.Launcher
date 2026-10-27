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
        var existingIds = (await repository.GetAllAsync(cancellationToken))
            .Select(instance => instance.Id)
            .ToHashSet();

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
            if (existingIds.Contains(instanceId))
            {
                logger.LogError("Preserving staged files for instance {InstanceId} because its catalog record still exists: {Directory}", instanceId, directory);
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

        instance.Name = instance.Name.Trim();
        instance.MinecraftVersion = instance.MinecraftVersion.Trim();

        if (instance.LoaderType == MinecraftLoaderType.Vanilla)
            instance.LoaderVersion = null;

        await repository.SaveAsync(instance, cancellationToken);
    }

    public async Task<MinecraftInstance> DuplicateAsync(
        MinecraftInstance source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
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
            await UpdateAsync(duplicate, cancellationToken);
            return duplicate;
        }
        catch
        {
            await repository.DeleteAsync(duplicate.Id, CancellationToken.None);
            if (Directory.Exists(duplicate.GameDirectory) && !Directory.EnumerateFileSystemEntries(duplicate.GameDirectory).Any())
                Directory.Delete(duplicate.GameDirectory);
            var root = Path.GetDirectoryName(duplicate.GameDirectory);
            if (root is not null && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
            throw;
        }
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
