using System.Text.Json;
using System.Security.Cryptography;
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
    public DateTimeOffset? LastManifestUpdate { get; private set; }
    public bool IsUsingCachedManifest { get; private set; }
    public bool IsOfflineFallback { get; private set; }

    public async Task<int?> GetRequiredJavaMajorVersionAsync(string minecraftVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("Укажи версию Minecraft.", nameof(minecraftVersion));

        var version = (await GetVersionsAsync(cancellationToken))
            .FirstOrDefault(item => item.Id.Equals(minecraftVersion.Trim(), StringComparison.OrdinalIgnoreCase));
        if (version?.ManifestUrl is null || !Uri.TryCreate(version.ManifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        paths.EnsureCreated();
        var cacheName = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(version.Id))).ToLowerInvariant();
        var detailCachePath = Path.Combine(paths.Cache, $"version-java-{cacheName}.json");
        if (File.Exists(detailCachePath))
        {
            try
            {
                var cachedMajor = await ReadJavaMajorVersionAsync(detailCachePath, cancellationToken);
                if (cachedMajor is > 0) return cachedMajor;
            }
            catch (JsonException) { }
            catch (IOException) { }
        }

        var payload = await httpClient.GetByteArrayAsync(uri, cancellationToken);
        if (!string.IsNullOrWhiteSpace(version.Sha1))
        {
            var actualHash = Convert.ToHexString(SHA1.HashData(payload));
            if (!actualHash.Equals(version.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Контрольная сумма метаданных Minecraft {version.Id} не совпала.");
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("javaVersion", out var javaVersion) ||
            !javaVersion.TryGetProperty("majorVersion", out var majorElement) ||
            !majorElement.TryGetInt32(out var major) || major <= 0)
            return null;

        var temporaryPath = detailCachePath + ".part";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, payload, cancellationToken);
            File.Move(temporaryPath, detailCachePath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        return major;
    }

    private static async Task<int?> ReadJavaMajorVersionAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("javaVersion", out var javaVersion) &&
               javaVersion.TryGetProperty("majorVersion", out var majorElement) &&
               majorElement.TryGetInt32(out var major) && major > 0 ? major : null;
    }

    public async Task<IReadOnlyList<GameVersionInfo>> GetVersionsAsync(CancellationToken cancellationToken = default)
        => await GetVersionsAsync(forceRefresh: false, cancellationToken);

    public async Task<IReadOnlyList<GameVersionInfo>> RefreshVersionsAsync(CancellationToken cancellationToken = default)
        => await GetVersionsAsync(forceRefresh: true, cancellationToken);

    private async Task<IReadOnlyList<GameVersionInfo>> GetVersionsAsync(bool forceRefresh, CancellationToken cancellationToken)
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
                    LastManifestUpdate = new DateTimeOffset(File.GetLastWriteTimeUtc(CachePath), TimeSpan.Zero);
                    if (!forceRefresh && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < CacheLifetime)
                    {
                        IsUsingCachedManifest = true;
                        IsOfflineFallback = false;
                        return cachedVersions;
                    }
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
                IsUsingCachedManifest = false;
                IsOfflineFallback = false;
                LastManifestUpdate = new DateTimeOffset(File.GetLastWriteTimeUtc(CachePath), TimeSpan.Zero);
                return await ReadManifestAsync(CachePath, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
            {
                if (cachedVersions is not null)
                {
                    IsUsingCachedManifest = true;
                    IsOfflineFallback = true;
                    return cachedVersions;
                }
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
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in versions.EnumerateArray())
        {
            var id = entry.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            var type = entry.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type) || !seenIds.Add(id)) continue;
            DateTimeOffset? releaseTime = entry.TryGetProperty("releaseTime", out var timeValue) && timeValue.TryGetDateTimeOffset(out var parsedTime) ? parsedTime : null;
            var url = entry.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
            var sha1 = entry.TryGetProperty("sha1", out var hashValue) ? hashValue.GetString() : null;
            output.Add(new GameVersionInfo(id, type, releaseTime, url, sha1));
        }
        if (output.Count == 0)
            throw new JsonException("Манифест Minecraft не содержит допустимых версий.");
        return output;
    }
}
