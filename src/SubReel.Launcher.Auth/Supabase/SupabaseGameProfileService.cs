using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SubReel.Launcher.Abstractions.Game;

namespace SubReel.Launcher.Auth.Supabase;

/// <summary>
/// Links only non-secret Minecraft profile metadata to the currently authenticated SubReel user.
/// Never sends Minecraft access tokens, Microsoft refresh tokens, or Xbox tokens to Supabase.
/// </summary>
public sealed class SupabaseGameProfileService : ISupabaseGameProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _baseUrl;
    private readonly string _publishableKey;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISubReelAuthService _subReelAuth;
    private readonly IGameAuthService _gameAuth;

    public SupabaseGameProfileService(IConfiguration configuration, IHttpClientFactory httpClientFactory,
        ISubReelAuthService subReelAuth, IGameAuthService gameAuth)
    {
        _baseUrl = (configuration["Platform:SupabaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
        _publishableKey = (configuration["Platform:SupabasePublishableKey"] ?? string.Empty).Trim();
        _httpClientFactory = httpClientFactory;
        _subReelAuth = subReelAuth;
        _gameAuth = gameAuth;
    }

    public async Task<LinkedGameProfile?> GetLinkedProfileAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetValidPlatformSessionAsync(cancellationToken);
        using var response = await SendAsync(HttpMethod.Get,
            $"/rest/v1/launcher_game_profiles?select=minecraft_uuid,minecraft_username,updated_at&user_id=eq.{Uri.EscapeDataString(session.UserId)}",
            null, session.AccessToken, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, payload);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "[]" : payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            return null;
        var item = document.RootElement[0];
        if (!TryReadString(item, "minecraft_uuid", out var uuid) || !TryReadString(item, "minecraft_username", out var username))
            return null;
        DateTimeOffset? updatedAt = item.TryGetProperty("updated_at", out var updated) && updated.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(updated.GetString(), out var parsed) ? parsed : null;
        return new LinkedGameProfile(uuid, username, updatedAt);
    }

    public async Task<LinkedGameProfile> LinkCurrentMicrosoftProfileAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetValidPlatformSessionAsync(cancellationToken);
        var account = _gameAuth.ActiveAccount;
        if (account is null)
            throw new InvalidOperationException("Сначала добавь или выбери игровой аккаунт Minecraft.");
        if (account.Type != LauncherAccountType.Microsoft)
            throw new InvalidOperationException("Привязать можно только подтверждённый Microsoft-аккаунт Minecraft Java. Офлайн-профили не подтверждают лицензию.");
        if (string.IsNullOrWhiteSpace(account.Uuid) || string.IsNullOrWhiteSpace(account.Username))
            throw new InvalidOperationException("У выбранного игрового аккаунта отсутствуют данные профиля.");

        // Revalidate the Minecraft session before linking persisted account metadata.
        var gameSession = await _gameAuth.GetActiveSessionAsync(cancellationToken);
        if (gameSession is null || !string.Equals(gameSession.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Не удалось подтвердить текущую игровую сессию Minecraft. Повтори вход через Microsoft.");

        var linked = new LinkedGameProfile(gameSession.Uuid, gameSession.Username, DateTimeOffset.UtcNow);
        var body = new[] { new { user_id = session.UserId, minecraft_uuid = linked.MinecraftUuid,
            minecraft_username = linked.MinecraftUsername, updated_at = DateTimeOffset.UtcNow } };
        using var response = await SendAsync(HttpMethod.Post,
            "/rest/v1/launcher_game_profiles?on_conflict=user_id", body, session.AccessToken, cancellationToken,
            "resolution=merge-duplicates,return=minimal");
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, payload);
        return linked;
    }

    public async Task UnlinkProfileAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetValidPlatformSessionAsync(cancellationToken);
        using var response = await SendAsync(HttpMethod.Delete,
            $"/rest/v1/launcher_game_profiles?user_id=eq.{Uri.EscapeDataString(session.UserId)}",
            null, session.AccessToken, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, payload);
    }

    private async Task<SubReelAuthSession> GetValidPlatformSessionAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(_publishableKey))
            throw new InvalidOperationException("Supabase не настроен. Проверь Platform:SupabaseUrl и Platform:SupabasePublishableKey.");
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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body,
        string accessToken, CancellationToken cancellationToken, string? prefer = null)
    {
        var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Add("apikey", _publishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (prefer is not null) request.Headers.TryAddWithoutValidation("Prefer", prefer);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        try { return await _httpClientFactory.CreateClient("SupabaseAuth").SendAsync(request, cancellationToken); }
        finally { request.Dispose(); }
    }

    private static Exception CreateApiException(HttpStatusCode status, string payload)
    {
        // Do not expose request headers or bearer tokens in errors.
        var detail = payload.Length > 500 ? payload[..500] : payload;
        return new InvalidOperationException($"Supabase не выполнил операцию с игровым профилем (HTTP {(int)status}). {detail}");
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value = property.GetString()!);
    }
}
