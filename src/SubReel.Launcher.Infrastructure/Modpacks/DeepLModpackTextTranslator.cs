using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Abstractions.Paths;

namespace SubReel.Launcher.Infrastructure.Modpacks;

/// <summary>Calls SubReel's authenticated translation proxy; DeepL credentials stay on the server.</summary>
public sealed class DeepLModpackTextTranslator(HttpClient http, ILauncherSessionProvider session, IAppPaths paths) : IModpackTextTranslator
{
    private const int MaxCacheEntries = 10_000;
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private Dictionary<string, string>? _cache;
    private string CachePath => Path.Combine(paths.Cache, "modpack-translations-v1.json");

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureCacheLoadedAsync(cancellationToken);
            var cache = _cache!;
            var language = targetLanguage.Trim().ToUpperInvariant();
            var result = new string[texts.Count];
            var missing = new Dictionary<string, (string Text, List<int> Indexes)>(StringComparer.Ordinal);
            for (var index = 0; index < texts.Count; index++)
            {
                var text = texts[index] ?? string.Empty;
                var cacheKey = GetCacheKey(language, text);
                if (cache.TryGetValue(cacheKey, out var translated)) result[index] = translated;
                else if (missing.TryGetValue(cacheKey, out var existing)) existing.Indexes.Add(index);
                else missing.Add(cacheKey, (text, [index]));
            }

            if (missing.Count == 0) return result;

            var accessToken = session.AccessToken;
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException("Войдите в аккаунт SubReel, чтобы переводить описания каталога.");

            using var request = new HttpRequestMessage(HttpMethod.Post, "translate");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = JsonContent.Create(new { text = missing.Values.Select(value => value.Text).ToArray(), target_lang = language });
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = "Сервис перевода SubReel временно недоступен.";
                try
                {
                    using var errorDocument = JsonDocument.Parse(body);
                    if (errorDocument.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                        message = error.GetString() ?? message;
                }
                catch (JsonException) { }
                throw new HttpRequestException(message, null, response.StatusCode);
            }

            using var document = JsonDocument.Parse(body);
            var translations = document.RootElement.GetProperty("translations").EnumerateArray().ToArray();
            if (translations.Length != missing.Count)
                throw new InvalidDataException("DeepL вернул неожиданное количество переводов.");

            var cacheChanged = false;
            var missingEntries = missing.ToArray();
            for (var index = 0; index < missingEntries.Length; index++)
            {
                var text = translations[index].GetProperty("text").GetString() ?? missingEntries[index].Value.Text;
                cache[missingEntries[index].Key] = text;
                foreach (var resultIndex in missingEntries[index].Value.Indexes) result[resultIndex] = text;
                cacheChanged = true;
            }

            if (cache.Count > MaxCacheEntries)
                foreach (var staleKey in cache.Keys.Take(cache.Count - MaxCacheEntries).ToArray()) cache.Remove(staleKey);
            if (cacheChanged) await SaveCacheAsync(cancellationToken);
            return result;
        }
        finally { _cacheGate.Release(); }
    }

    private async Task EnsureCacheLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null) return;
        paths.EnsureCreated();
        if (!File.Exists(CachePath))
        {
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
            return;
        }

        await using var stream = new FileStream(CachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        _cache = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, CacheJsonOptions, cancellationToken)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task SaveCacheAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = CachePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await JsonSerializer.SerializeAsync(stream, _cache, CacheJsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, CachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string GetCacheKey(string language, string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(language + "\n" + text));
        return Convert.ToHexString(bytes);
    }
}
