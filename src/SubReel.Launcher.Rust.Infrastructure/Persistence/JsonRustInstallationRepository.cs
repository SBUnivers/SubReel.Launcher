using System.Text.Json;
using SubReel.Launcher.Rust.Abstractions.Models;
using SubReel.Launcher.Rust.Abstractions.Paths;
using SubReel.Launcher.Rust.Abstractions.Services;

namespace SubReel.Launcher.Rust.Infrastructure.Persistence;

public sealed class JsonRustInstallationRepository(IRustPaths paths) : IRustInstallationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<RustInstalledClient>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<RustInstalledClient?> GetAsync(string clientId, string releaseTag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseTag);
        return (await GetAllAsync(cancellationToken)).FirstOrDefault(item =>
            item.ClientId.Equals(clientId, StringComparison.OrdinalIgnoreCase) &&
            item.ReleaseTag.Equals(releaseTag, StringComparison.Ordinal));
    }

    public async Task SaveAsync(RustInstalledClient installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentException.ThrowIfNullOrWhiteSpace(installation.ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installation.ReleaseTag);
        if (installation.ArchiveSha256 is not { Length: 64 } || installation.FileSha256 is null || installation.FileSha256.Count == 0)
            throw new ArgumentException("Манифест установленного Rust-клиента неполон.", nameof(installation));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            paths.EnsureCreated();
            var records = (await ReadAsync(cancellationToken)).ToList();
            var index = records.FindIndex(item =>
                item.ClientId.Equals(installation.ClientId, StringComparison.OrdinalIgnoreCase) &&
                item.ReleaseTag.Equals(installation.ReleaseTag, StringComparison.Ordinal));
            if (index < 0) records.Add(installation); else records[index] = installation;
            await WriteAtomicallyAsync(records, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string clientId, string releaseTag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseTag);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            paths.EnsureCreated();
            var records = (await ReadAsync(cancellationToken)).Where(item =>
                !item.ClientId.Equals(clientId, StringComparison.OrdinalIgnoreCase) ||
                !item.ReleaseTag.Equals(releaseTag, StringComparison.Ordinal)).ToArray();
            await WriteAtomicallyAsync(records, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<RustInstalledClient>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.InstalledClientsFile)) return [];
        await using var stream = new FileStream(paths.InstalledClientsFile, FileMode.Open, FileAccess.Read,
            FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var records = await JsonSerializer.DeserializeAsync<List<RustInstalledClient>>(stream, JsonOptions, cancellationToken);
        if (records is null || records.Any(item => string.IsNullOrWhiteSpace(item.ClientId) ||
                string.IsNullOrWhiteSpace(item.ReleaseTag) || item.FileSha256 is null) ||
            records.Select(item => $"{item.ClientId}\n{item.ReleaseTag}").Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Count)
            throw new InvalidDataException("Файл установленных Rust-клиентов повреждён или содержит повторы.");
        return records;
    }

    private async Task WriteAtomicallyAsync(IReadOnlyCollection<RustInstalledClient> records, CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(paths.InstalledClientsFile);
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
