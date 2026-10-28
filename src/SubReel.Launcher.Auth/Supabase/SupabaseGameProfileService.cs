using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SubReel.Launcher.Abstractions.Game;

namespace SubReel.Launcher.Auth.Supabase;

/// <summary>
/// Links verified Minecraft profile metadata through the SubReel website API.
/// Minecraft access tokens are sent only to the verification endpoint and are never logged or persisted.
/// </summary>
public sealed class SupabaseGameProfileService : ISupabaseGameProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _apiBaseUrl;
    private readonly string _publishableKey;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISubReelAuthService _subReelAuth;
    private readonly IGameAuthService _gameAuth;

    public SupabaseGameProfileService(IConfiguration configuration, IHttpClientFactory httpClientFactory,
        ISubReelAuthService subReelAuth, IGameAuthService gameAuth)
    {
        _apiBaseUrl = (configuration["Platform:ApiBaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
        _publishableKey = (configuration["Platform:SupabasePublishableKey"] ?? string.Empty).Trim();
        _httpClientFactory = httpClientFactory;
        _subReelAuth = subReelAuth;
        _gameAuth = gameAuth;
    }

    public async Task<IReadOnlyList<LinkedGameProfile>> GetLinkedProfilesAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetValidPlatformSessionAsync(cancellationToken);
        using var response = await SendApiAsync(HttpMethod.Get, "minecraft-profiles", null,
            session.AccessToken, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, payload);

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
        if (!document.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<LinkedGameProfile>();
        foreach (var item in profiles.EnumerateArray())
        {
            if (!TryReadString(item, "minecraftUuid", out var uuid) ||
                !TryReadString(item, "minecraftUsername", out var username)) continue;
            DateTimeOffset? linkedAt = item.TryGetProperty("linkedAt", out var updated) &&
                updated.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(updated.GetString(), out var parsed) ? parsed : null;
            result.Add(new LinkedGameProfile(uuid, username, linkedAt));
        }
        return result;
    }

    public async Task<LinkedGameProfile> LinkCurrentMicrosoftProfileAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetValidPlatformSessionAsync(cancellationToken);
        var account = _gameAuth.ActiveAccount;
        if (account is null)
            throw new InvalidOperationException("Сначала добавь или выбери игровой аккаунт Minecraft.");
        if (account.Type != LauncherAccountType.Microsoft)
            throw new InvalidOperationException("Привязать можно только подтверждённый Microsoft-аккаунт Minecraft Java. Офлайн-профили не подтверждают лицензию.");

        // Refresh/revalidate the active Minecraft session. GameSession.AccessToken is the Minecraft Services token.
        var gameSession = await _gameAuth.GetActiveSessionAsync(cancellationToken);
        if (gameSession is null ||
            !NormalizeUuid(gameSession.Uuid).Equals(NormalizeUuid(account.Uuid), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Не удалось подтвердить текущую игровую сессию Minecraft. Повтори вход через Microsoft.");
        if (string.IsNullOrWhiteSpace(gameSession.AccessToken))
            throw new InvalidOperationException("Minecraft не выдал токен проверки профиля. Войди через Microsoft ещё раз.");

        var body = new { minecraftAccessToken = gameSession.AccessToken };
        using var response = await SendApiAsync(HttpMethod.Post, "minecraft-profiles", body,
            session.AccessToken, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, payload);

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("profile", out var profile) ||
            !TryReadString(profile, "minecraftUuid", out var uuid) ||
            !TryReadString(profile, "minecraftUsername", out var username))
            throw new InvalidOperationException("Сайт SubReel вернул некорректный ответ при привязке профиля.");
        DateTimeOffset? linkedAt = profile.TryGetProperty("linkedAt", out var linked) &&
            linked.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(linked.GetString(), out var parsed) ? parsed : null;
        return new LinkedGameProfile(uuid, username, linkedAt);
    }

    public async Task UnlinkProfileAsync(string minecraftUuid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftUuid) || !IsUuid(minecraftUuid))
            throw new ArgumentException("Укажи корректный UUID Minecraft-профиля.", nameof(minecraftUuid));
        var session = await GetValidPlatformSessionAsync(cancellationToken);
        using var response = await SendApiAsync(HttpMethod.Delete,
            $"minecraft-profiles/{Uri.EscapeDataString(minecraftUuid)}", null, session.AccessToken, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, payload);
    }

    private async Task<SubReelAuthSession> GetValidPlatformSessionAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_apiBaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrWhiteSpace(uri.UserInfo) ||
            string.IsNullOrWhiteSpace(_publishableKey))
            throw new InvalidOperationException("API SubReel не настроен. Проверь Platform:ApiBaseUrl и Platform:SupabasePublishableKey.");
        var session = _subReelAuth.CurrentSession;
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            var restored = await _subReelAuth.RestoreSessionAsync(cancellationToken);
            if (!restored.Success || restored.Session is null)
                throw new InvalidOperationException("Сначала войди в аккаунт SubReel, затем повтори действие.");
            session = restored.Session;
        }
        return session;
    }

    private async Task<HttpResponseMessage> SendApiAsync(HttpMethod method, string path, object? body,
        string accessToken, CancellationToken cancellationToken)
    {
        var baseUrl = _apiBaseUrl.EndsWith("/api/launcher", StringComparison.OrdinalIgnoreCase)
            ? _apiBaseUrl
            : _apiBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/{path}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(_publishableKey))
            request.Headers.Add("apikey", _publishableKey);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        try { return await _httpClientFactory.CreateClient("SupabaseAuth").SendAsync(request, cancellationToken); }
        finally { request.Dispose(); }
    }

    private static Exception CreateApiException(HttpStatusCode status, string payload)
    {
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                code = error.GetString();
        }
        catch (JsonException) { }

        return code switch
        {
            "UNAUTHENTICATED" => new InvalidOperationException("Сессия SubReel истекла. Войди в аккаунт снова."),
            "MINECRAFT_PROFILE_UNVERIFIED" => new InvalidOperationException("Не удалось подтвердить профиль Minecraft. Войди в лицензионный аккаунт Microsoft ещё раз."),
            "MINECRAFT_VERIFICATION_UNAVAILABLE" => new InvalidOperationException("Сервис проверки Minecraft временно недоступен. Попробуй позже."),
            "MINECRAFT_PROFILE_ALREADY_LINKED" => new InvalidOperationException("Этот Minecraft-профиль уже привязан к аккаунту SubReel."),
            "MINECRAFT_PROFILE_NOT_LINKED" => new InvalidOperationException("Этот Minecraft-профиль уже отвязан или не принадлежит текущему аккаунту."),
            "VALIDATION_ERROR" => new InvalidOperationException("Сайт отклонил данные профиля. Обнови лаунчер и повтори."),
            _ when status == HttpStatusCode.Forbidden => new InvalidOperationException("Сайт SubReel запретил операцию с игровым профилем."),
            _ when (int)status >= 500 => new InvalidOperationException("Сервис SubReel временно недоступен. Попробуй позже."),
            _ when status == HttpStatusCode.NotFound => new InvalidOperationException("API управления Minecraft-профилями не найден. Проверь версию сайта SubReel."),
            _ when status == HttpStatusCode.TooManyRequests => new InvalidOperationException("Слишком много запросов к SubReel. Подожди немного и повтори."),
            _ => new InvalidOperationException($"Не удалось выполнить операцию с игровым профилем (HTTP {(int)status}).")
        };
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value = property.GetString()!);
    }

    private static string NormalizeUuid(string uuid) => uuid.Replace("-", string.Empty, StringComparison.Ordinal);
    private static bool IsUuid(string value) => System.Text.RegularExpressions.Regex.IsMatch(NormalizeUuid(value), "^[0-9a-fA-F]{32}$");
}
