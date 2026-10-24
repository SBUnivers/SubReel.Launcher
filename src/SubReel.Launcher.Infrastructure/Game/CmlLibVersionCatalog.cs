using System.Text.Json;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Abstractions.Paths;

namespace SubReel.Launcher.Infrastructure.Game;

/// <summary>Hourly cached official Mojang version manifest with offline cache fallback.</summary>
public sealed class CmlLibVersionCatalog(HttpClient httpClient, IAppPaths paths) : IGameVersionCatalog
{
    private static readonly Uri ManifestUri = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string CachePath => Path.Combine(paths.Cache, "version_manifest_v2.json");

    public async Task<IReadOnlyList<GameVersionInfo>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            paths.EnsureCreated();
            IReadOnlyList<GameVersionInfo>? cachedVersions = null;
            if (File.Exists(CachePath))
            {
                try
                {
                    cachedVersions = await ReadManifestAsync(CachePath, cancellationToken);
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < CacheLifetime)
                        return cachedVersions;
                }
                catch (JsonException)
                {
                    // Keep the corrupt file in place until a valid replacement has been downloaded.
                }
            }

            try
            {
                using var response = await httpClient.GetAsync(ManifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var temporaryPath = CachePath + ".part";
                try
                {
                    await using (var target = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        await networkStream.CopyToAsync(target, cancellationToken);
                        await target.FlushAsync(cancellationToken);
                    }
                    _ = await ReadManifestAsync(temporaryPath, cancellationToken);
                    File.Move(temporaryPath, CachePath, overwrite: true);
                }
                finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                return await ReadManifestAsync(CachePath, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
            {
                if (cachedVersions is not null)
                    return cachedVersions;
                throw new InvalidOperationException("Не удалось получить официальный манифест версий Minecraft и нет корректного локального кэша.", ex);
            }
        }
        finally { _gate.Release(); }
    }

    private static async Task<IReadOnlyList<GameVersionInfo>> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
            throw new JsonException("Поле versions отсутствует в манифесте Mojang.");

        var output = new List<GameVersionInfo>(versions.GetArrayLength());
        foreach (var entry in versions.EnumerateArray())
        {
            var id = entry.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            var type = entry.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type)) continue;
            DateTimeOffset? releaseTime = entry.TryGetProperty("releaseTime", out var timeValue) && timeValue.TryGetDateTimeOffset(out var parsedTime) ? parsedTime : null;
            var url = entry.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
            var sha1 = entry.TryGetProperty("sha1", out var hashValue) ? hashValue.GetString() : null;
            output.Add(new GameVersionInfo(id, type, releaseTime, url, sha1));
        }
        return output;
    }
}
