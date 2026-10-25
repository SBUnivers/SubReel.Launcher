using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SubReel.Launcher.Abstractions.Modpacks;

namespace SubReel.Launcher.Infrastructure.Modpacks;

/// <summary>DeepL API Free adapter. The API key is read from a per-user DPAPI-protected file.</summary>
public sealed class DeepLModpackTextTranslator(HttpClient http, ITranslationKeyStore keys) : IModpackTextTranslator
{
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];
        var key = await keys.GetAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Добавьте ключ DeepL API в настройках лаунчера.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v2/translate");
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
        request.Content = JsonContent.Create(new { text = texts, target_lang = targetLanguage });
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepL вернул {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("translations").EnumerateArray()
            .Select((translation, index) => translation.GetProperty("text").GetString() ?? texts[index])
            .ToArray();
    }
}
