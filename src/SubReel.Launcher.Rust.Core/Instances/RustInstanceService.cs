using SubReel.Launcher.Rust.Abstractions.Models;
using SubReel.Launcher.Rust.Abstractions.Paths;
using SubReel.Launcher.Rust.Abstractions.Services;

namespace SubReel.Launcher.Rust.Core.Instances;

public sealed class RustInstanceService(IRustInstanceRepository repository, IRustPaths paths)
{
    public Task<IReadOnlyList<RustInstanceRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
        repository.GetAllAsync(cancellationToken);

    public async Task<RustInstanceRecord> CreateAsync(
        string clientId,
        string name,
        string releaseTag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseTag);
        var normalizedName = name.Trim();
        if (normalizedName.Length > 64)
            throw new ArgumentException("Имя Rust-инстанса не может быть длиннее 64 символов.", nameof(name));
        if (normalizedName.Any(char.IsControl))
            throw new ArgumentException("Имя Rust-инстанса содержит недопустимые управляющие символы.", nameof(name));

        var record = new RustInstanceRecord(Guid.NewGuid(), clientId.Trim(), normalizedName,
            releaseTag.Trim(), DateTimeOffset.UtcNow);
        await repository.SaveAsync(record, cancellationToken);
        return record;
    }

    public async Task<RustInstanceRecord?> MarkPlayedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Не задан ID Rust-инстанса.", nameof(id));
        var instance = (await repository.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Id == id);
        if (instance is null) return null;
        var updated = instance with { LastPlayedAt = DateTimeOffset.UtcNow };
        await repository.SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<RustInstanceRecord> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Не задан ID Rust-инстанса.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalizedName = name.Trim();
        if (normalizedName.Length > 64 || normalizedName.Any(char.IsControl))
            throw new ArgumentException("Название Rust-инстанса должно содержать от 1 до 64 обычных символов.", nameof(name));
        var instance = (await repository.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException("Rust-инстанс не найден.");
        var updated = instance with { Name = normalizedName };
        await repository.SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Не задан ID Rust-инстанса.", nameof(id));
        var instance = (await repository.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Id == id);
        if (instance is null) return;
        var dataDirectory = Path.GetFullPath(paths.GetInstanceDirectory(id));
        var instancesDirectory = Path.GetFullPath(paths.Instances);
        if (!string.Equals(Path.GetDirectoryName(dataDirectory), instancesDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь Rust-инстанса выходит за пределы папки инстансов.");

        var tombstone = dataDirectory + ".deleting-" + Guid.NewGuid().ToString("N");
        var movedData = Directory.Exists(dataDirectory);
        if (movedData) EnsureNoReparsePoints(dataDirectory);
        if (movedData) Directory.Move(dataDirectory, tombstone);
        try
        {
            await repository.DeleteAsync(id, cancellationToken);
        }
        catch
        {
            if (movedData && Directory.Exists(tombstone) && !Directory.Exists(dataDirectory))
                Directory.Move(tombstone, dataDirectory);
            throw;
        }

        if (movedData && Directory.Exists(tombstone))
        {
            try { Directory.Delete(tombstone, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<int> RecoverInterruptedDeletesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.Instances)) return 0;
        var records = await repository.GetAllAsync(cancellationToken);
        var recordIds = records.Select(item => item.Id).ToHashSet();
        var recovered = 0;
        foreach (var tombstone in Directory.EnumerateDirectories(paths.Instances, "*.deleting-*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(tombstone);
            var marker = name.IndexOf(".deleting-", StringComparison.Ordinal);
            if (marker != 32 || name.Length != marker + ".deleting-".Length + 32 ||
                !Guid.TryParseExact(name[..marker], "N", out var id) ||
                !Guid.TryParseExact(name[(marker + ".deleting-".Length)..], "N", out _))
                continue;
            if (!HasNoReparsePoints(tombstone)) continue;

            var original = paths.GetInstanceDirectory(id);
            if (recordIds.Contains(id))
            {
                if (!Directory.Exists(original)) Directory.Move(tombstone, original);
                else Directory.Delete(tombstone, recursive: true);
            }
            else
            {
                Directory.Delete(tombstone, recursive: true);
            }
            recovered++;
        }
        return recovered;
    }

    private static void EnsureNoReparsePoints(string root)
    {
        if (!HasNoReparsePoints(root))
            throw new IOException("Нельзя удалить Rust-инстанс, содержащий ссылку на другой путь.");
    }

    private static bool HasNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        return true;
    }
}
