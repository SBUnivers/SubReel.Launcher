using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace SubReel.Launcher.App.Services;

public sealed record PlatformHealthConfiguration(
    [property: JsonPropertyName("publicNews")] bool PublicNews,
    [property: JsonPropertyName("launcherSignIn")] bool LauncherSignIn,
    [property: JsonPropertyName("curseForgeCatalog")] bool CurseForgeCatalog,
    [property: JsonPropertyName("catalogTranslation")] bool CatalogTranslation);

public sealed record PlatformHealthResponse(
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checkedAt")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("configuration")] PlatformHealthConfiguration Configuration);

public interface IPlatformHealthService
{
    bool IsConfigured { get; }
    Task<PlatformHealthResponse> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed class PlatformHealthService(HttpClient http, IConfiguration configuration) : IPlatformHealthService
{
    private readonly string _apiBaseUrl = (configuration["Platform:ApiBaseUrl"] ?? "https://www.subreel.ru/api/launcher/").TrimEnd('/') + "/";

    public bool IsConfigured => Uri.TryCreate(_apiBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public async Task<PlatformHealthResponse> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("Адрес SubReel Platform не настроен или не использует HTTPS.");
        using var response = await http.GetAsync(new Uri(new Uri(_apiBaseUrl), "health"), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Health API SubReel вернул HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<PlatformHealthResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Health API SubReel вернул пустой ответ.");
    }
}
