using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Abstractions.Paths;

namespace SubReel.Launcher.Auth.Supabase;

/// <summary>Supabase Auth REST client. Stores refresh/access tokens encrypted with Windows DPAPI.</summary>
[SupportedOSPlatform("windows")]
public sealed class SupabaseAuthService : ISubReelAuthService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SubReel.Launcher.SupabaseSession.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _baseUrl;
    private readonly string _publishableKey;
    private readonly string _sessionPath;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SupabaseAuthService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SubReelAuthSession? _session;

    public SupabaseAuthService(IConfiguration configuration, IAppPaths paths,
        IHttpClientFactory httpClientFactory, ILogger<SupabaseAuthService> logger)
    {
        _baseUrl = (configuration["Platform:SupabaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
        _publishableKey = (configuration["Platform:SupabasePublishableKey"] ?? string.Empty).Trim();
        _sessionPath = Path.Combine(paths.Auth, "subreel-session.dpapi");
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsConfigured => Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(_publishableKey);
    public SubReelAuthSession? CurrentSession => _session;

    public async Task<SubReelAuthResult> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        ValidateCredentials(email, password);
        if (!IsConfigured) return new(false, false, "Supabase не настроен. Укажи Platform:SupabaseUrl и Platform:SupabasePublishableKey.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=password",
                new { email = email.Trim(), password }, null, cancellationToken);
            var payload = await ReadPayloadAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(false, false, FriendlyError(payload, response.StatusCode));
            var session = ParseSession(payload);
            if (session is null) return new(false, false, "Supabase не вернул полноценную сессию пользователя.");
            await SaveSessionAsync(session, cancellationToken);
            _session = session;
            return new(true, false, "Вход выполнен.", session);
        }
        finally { _gate.Release(); }
    }

    public async Task<SubReelAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        ValidateCredentials(email, password);
        if (!IsConfigured) return new(false, false, "Supabase не настроен. Укажи Platform:SupabaseUrl и Platform:SupabasePublishableKey.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "/auth/v1/signup",
                new { email = email.Trim(), password }, null, cancellationToken);
            var payload = await ReadPayloadAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(false, false, FriendlyError(payload, response.StatusCode));
            var session = ParseSession(payload);
            if (session is not null)
            {
                await SaveSessionAsync(session, cancellationToken);
                _session = session;
                return new(true, false, "Регистрация выполнена.", session);
            }
            return new(true, true, "Аккаунт создан. Подтверди email по ссылке из письма, затем войди в SubReel.");
        }
        finally { _gate.Release(); }
    }

    public async Task<SubReelAuthResult> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return new(false, false, "Supabase не настроен.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await LoadSessionAsync(cancellationToken);
            if (stored is null) return new(false, false, "Сохранённой сессии нет.");
            if (stored.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                _session = stored;
                return new(true, false, "Сессия восстановлена.", stored);
            }
            using var response = await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token",
                new { refresh_token = stored.RefreshToken }, null, cancellationToken);
            var payload = await ReadPayloadAsync(response, cancellationToken);
            var refreshed = response.IsSuccessStatusCode ? ParseSession(payload) : null;
            if (refreshed is null)
            {
                _session = null;
                TryDeleteSession();
                return new(false, false, "Сессия истекла. Войди в аккаунт снова.");
            }
            await SaveSessionAsync(refreshed, cancellationToken);
            _session = refreshed;
            return new(true, false, "Сессия обновлена.", refreshed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось восстановить сессию SubReel");
            return new(false, false, "Не удалось восстановить сессию. Проверь подключение к интернету.");
        }
        finally { _gate.Release(); }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = _session ?? await LoadSessionAsync(cancellationToken);
            if (session is not null && IsConfigured)
            {
                try
                {
                    using var response = await SendAsync(HttpMethod.Post, "/auth/v1/logout", null, session.AccessToken, cancellationToken);
                    if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Unauthorized)
                        _logger.LogWarning("Supabase logout returned HTTP {StatusCode}", (int)response.StatusCode);
                }
                catch (HttpRequestException ex) { _logger.LogWarning(ex, "Удалённый выход Supabase не удался; локальная сессия будет удалена"); }
            }
            _session = null;
            TryDeleteSession();
        }
        finally { _gate.Release(); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? accessToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("SupabaseAuth");
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Add("apikey", _publishableKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(accessToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }

    private static async Task<JsonDocument> ReadPayloadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var content = await response.Content.ReadAsStringAsync(ct);
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }

    private static SubReelAuthSession? ParseSession(JsonDocument payload)
    {
        var root = payload.RootElement;
        if (!TryString(root, "access_token", out var access) || !TryString(root, "refresh_token", out var refresh)) return null;
        var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 3600;
        if (!root.TryGetProperty("user", out var user) || !TryString(user, "id", out var id)) return null;
        var email = TryString(user, "email", out var emailValue) ? emailValue : null;
        return new(access, refresh, DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(expiresIn, 60, 60 * 60 * 24)), id, email);
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value = property.GetString()!);
    }

    private static string FriendlyError(JsonDocument payload, HttpStatusCode status)
    {
        var root = payload.RootElement;
        foreach (var key in new[] { "msg", "message", "error_description", "error" })
            if (TryString(root, key, out var message))
            {
                var normalized = message.ToLowerInvariant();
                if (normalized.Contains("invalid login credentials")) return "Неверный email или пароль.";
                if (normalized.Contains("email not confirmed")) return "Подтверди email по ссылке из письма.";
                if (normalized.Contains("user already registered")) return "Пользователь с таким email уже зарегистрирован.";
                if (normalized.Contains("password should be at least")) return "Пароль слишком короткий. Используй не менее 6 символов.";
                return message.Length > 240 ? message[..240] : message;
            }
        return status switch
        {
            HttpStatusCode.TooManyRequests => "Слишком много попыток. Подожди и попробуй снова.",
            HttpStatusCode.Unauthorized => "Не удалось подтвердить данные. Проверь email и пароль.",
            _ => $"Ошибка Supabase Auth (HTTP {(int)status}). Проверь настройки проекта."
        };
    }

    private async Task SaveSessionAsync(SubReelAuthSession session, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(session, JsonOptions));
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_sessionPath, protectedBytes, ct);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private async Task<SubReelAuthSession?> LoadSessionAsync(CancellationToken ct)
    {
        if (!File.Exists(_sessionPath)) return null;
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_sessionPath, ct);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<SubReelAuthSession>(bytes, JsonOptions); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException)
        {
            _logger.LogWarning(ex, "Локальная сессия Supabase повреждена или недоступна; удаляем её");
            TryDeleteSession();
            return null;
        }
    }

    private void TryDeleteSession()
    {
        try { if (File.Exists(_sessionPath)) File.Delete(_sessionPath); }
        catch (IOException ex) { _logger.LogWarning(ex, "Не удалось удалить файл сессии Supabase"); }
    }

    private static void ValidateCredentials(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Length > 254)
            throw new ArgumentException("Укажи корректный email.", nameof(email));
        if (string.IsNullOrEmpty(password) || password.Length < 6)
            throw new ArgumentException("Пароль должен содержать не менее 6 символов.", nameof(password));
    }
}
