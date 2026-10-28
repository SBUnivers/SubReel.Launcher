using System.Text.Json;
using SubReel.Launcher.Rust.Abstractions.Models;
using SubReel.Launcher.Rust.Abstractions.Paths;
using SubReel.Launcher.Rust.Abstractions.Services;

namespace SubReel.Launcher.Rust.Infrastructure.Persistence;

public sealed class JsonRustInstanceRepository(IRustPaths paths) : IRustInstanceRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<RustInstanceRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(RustInstanceRecord instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Id == Guid.Empty) throw new ArgumentException("Не задан ID Rust-инстанса.", nameof(instance));
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.ReleaseTag);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            paths.EnsureCreated();
            var current = (await ReadAsync(cancellationToken)).ToList();
            var index = current.FindIndex(x => x.Id == instance.Id);
            if (index < 0) current.Add(instance); else current[index] = instance;
            await WriteAtomicallyAsync(current, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Не задан ID Rust-инстанса.", nameof(id));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            paths.EnsureCreated();
            var current = (await ReadAsync(cancellationToken)).Where(x => x.Id != id).ToArray();
            await WriteAtomicallyAsync(current, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<RustInstanceRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.InstanceIndexFile)) return [];
        await using var stream = new FileStream(paths.InstanceIndexFile, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var records = await JsonSerializer.DeserializeAsync<List<RustInstanceRecord>>(stream, JsonOptions, cancellationToken);
        if (records is null || records.Any(x => x.Id == Guid.Empty) || records.Select(x => x.Id).Distinct().Count() != records.Count)
            throw new InvalidDataException("Файл Rust-инстансов содержит пустые или повторяющиеся записи.");
        return records;
    }

    private async Task WriteAtomicallyAsync(IReadOnlyCollection<RustInstanceRecord> records, CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(paths.InstanceIndexFile);
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(target)) File.Move(temp, target, overwrite: true); else File.Move(temp, target);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
