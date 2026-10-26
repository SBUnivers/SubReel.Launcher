using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Models;

namespace SubReel.Launcher.Infrastructure.Persistence;

public sealed class JsonInstanceRepository : IInstanceRepository
{
    private readonly string _filePath;
    private readonly string _instancesRoot;
    private readonly ILogger<JsonInstanceRepository> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public JsonInstanceRepository(IAppPaths paths, ILogger<JsonInstanceRepository> logger)
    {
        paths.EnsureCreated();
        _filePath = Path.Combine(paths.Data, "instances.json");
        _instancesRoot = Path.GetFullPath(paths.Instances);
        _logger = logger;
    }

    public async Task<IReadOnlyList<MinecraftInstance>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            var instances = await ReadAllCoreAsync(cancellationToken);

            return instances
                .OrderByDescending(x => x.IsFavorite)
                .ThenByDescending(x => x.LastPlayedAt)
                .ThenBy(x => x.Name)
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<MinecraftInstance?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            var instances = await ReadAllCoreAsync(cancellationToken);

            return instances.FirstOrDefault(x => x.Id == id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Id == Guid.Empty)
            throw new ArgumentException(
                "Идентификатор сборки не может быть пустым.",
                nameof(instance));

        await _lock.WaitAsync(cancellationToken);

        try
        {
            var instances = await ReadAllCoreAsync(cancellationToken);
            var index = instances.FindIndex(x => x.Id == instance.Id);

            if (index >= 0)
                instances[index] = instance;
            else
                instances.Add(instance);

            // Resolve and validate the per-instance location before either file is
            // changed. Write the manifest first so an invalid path cannot leave a
            // catalog entry that points to nowhere.
            _ = GetInstanceDirectory(instance);
            await WriteInstanceManifestAsync(instance, cancellationToken);
            await WriteAllCoreAsync(instances, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            var instances = await ReadAllCoreAsync(cancellationToken);
            var removedInstance = instances.FirstOrDefault(x => x.Id == id);
            var removed = instances.RemoveAll(x => x.Id == id);

            if (removed > 0)
                await WriteAllCoreAsync(instances, cancellationToken);

            var manifestPath = GetInstanceManifestPath(id, removedInstance);
            if (manifestPath is not null && File.Exists(manifestPath)) File.Delete(manifestPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<MinecraftInstance>> ReadAllCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            var instances = await JsonSerializer.DeserializeAsync<List<MinecraftInstance>>(
                stream,
                JsonOptions,
                cancellationToken);
            if (instances is not null)
            {
                // Older JSON files can contain null rows, empty IDs, or duplicate
                // IDs after interrupted/manual edits. Keep the first valid record
                // for each ID so callers never get ambiguous instance selection.
                var seenIds = new HashSet<Guid>();
                var validInstances = new List<MinecraftInstance>(instances.Count);
                var rejectedCount = 0;

                foreach (var instance in instances)
                {
                    if (instance is null || instance.Id == Guid.Empty || !seenIds.Add(instance.Id))
                    {
                        rejectedCount++;
                        continue;
                    }

                    validInstances.Add(instance);
                }

                if (rejectedCount > 0)
                    _logger.LogWarning(
                        "При чтении списка инстансов пропущено {RejectedCount} пустых или повторяющихся записей; исходный JSON сохранён",
                        rejectedCount);

                return validInstances;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Не удалось прочитать список игровых инстансов; файл будет сохранён для восстановления");
        }

        var backupPath = $"{_filePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        File.Move(_filePath, backupPath);
        _logger.LogWarning("Повреждённый список инстансов перемещён в {BackupPath}; создан пустой список", backupPath);
        return [];
    }

    private async Task WriteAllCoreAsync(
        List<MinecraftInstance> instances,
        CancellationToken cancellationToken)
    {
        var temporaryPath = _filePath + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    instances,
                    JsonOptions,
                    cancellationToken);

                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task WriteInstanceManifestAsync(MinecraftInstance instance, CancellationToken cancellationToken)
    {
        var directory = GetInstanceDirectory(instance);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "instance.json");
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, instance, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private string GetInstanceDirectory(MinecraftInstance instance)
    {
        var gameDirectory = Path.GetFullPath(instance.GameDirectory);
        var relative = Path.GetRelativePath(_instancesRoot, gameDirectory);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Папка игры должна находиться внутри каталога инстансов лаунчера.");
        var instanceId = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return Path.Combine(_instancesRoot, instanceId);
    }

    private string? GetInstanceManifestPath(Guid id, MinecraftInstance? instance)
    {
        if (instance is null)
        {
            var path = Path.Combine(_instancesRoot, id.ToString("N"), "instance.json");
            return path;
        }
        return Path.Combine(GetInstanceDirectory(instance), "instance.json");
    }
}
