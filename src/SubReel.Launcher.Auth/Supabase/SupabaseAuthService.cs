using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Diagnostics;
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
    private sealed class LoopbackCallback(TcpClient client, Uri uri) : IDisposable
    {
        public TcpClient Client { get; } = client;
        public Uri Uri { get; } = uri;
        public void Dispose() => Client.Dispose();
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SubReel.Launcher.SupabaseSession.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _baseUrl;
    private readonly string _publishableKey;
    private readonly string _websiteUrl;
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
        _websiteUrl = (configuration["Platform:WebsiteUrl"] ?? "https://www.subreel.ru").Trim().TrimEnd('/');
        _sessionPath = Path.Combine(paths.Auth, "subreel-session.dpapi");
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsConfigured => Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(_publishableKey);
    public SubReelAuthSession? CurrentSession => _session;
    public bool IsBrowserSignInConfigured => IsConfigured && Uri.TryCreate(_websiteUrl, UriKind.Absolute, out var website) &&
        (website.Scheme == Uri.UriSchemeHttps || (website.IsLoopback && website.Scheme == Uri.UriSchemeHttp));

    public async Task<SubReelAuthResult> SignInWithBrowserAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBrowserSignInConfigured)
            return new(false, false, "Вход через сайт не настроен. Проверь Platform:WebsiteUrl и настройки Supabase.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var listenerPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var callbackUri = $"http://127.0.0.1:{listenerPort}/callback";

        try
        {
            using var startPayload = await PostPlatformJsonAsync("/api/launcher/auth/start", new
            {
                state,
                code_challenge = challenge,
                redirect_uri = callbackUri
            }, ct);
            var authorizationUrl = GetString(startPayload.RootElement, "authorization_url");
            if (!Uri.TryCreate(authorizationUrl, UriKind.Absolute, out var authUri) || authUri.Scheme != Uri.UriSchemeHttps && !authUri.IsLoopback)
                return new(false, false, "Сайт вернул некорректный адрес входа.");

            Process.Start(new ProcessStartInfo(authUri.ToString()) { UseShellExecute = true });
            using var callbackRequest = await ReceiveLoopbackCallbackAsync(listener, ct);
            var callbackUriResult = callbackRequest.Uri;
            var callbackState = GetQueryValue(callbackUriResult, "state");
            var code = GetQueryValue(callbackUriResult, "code");
            var callbackError = GetQueryValue(callbackUriResult, "error");
            var stateMatches = callbackState is not null && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(callbackState));
            await WriteCallbackResponseAsync(callbackRequest.Client.GetStream(),
                stateMatches && !string.IsNullOrWhiteSpace(code) ? "Вход подтверждён. Можно вернуться в SubReel Launcher." : "Вход не завершён. Вернись в лаунчер и попробуй ещё раз.",
                ct);
            if (!stateMatches) return new(false, false, "Проверка безопасности входа не пройдена. Повтори попытку.");
            if (!string.IsNullOrWhiteSpace(callbackError)) return new(false, false, "Вход через сайт отменён или истёк.");
            if (string.IsNullOrWhiteSpace(code) || code.Length > 128) return new(false, false, "Сайт не вернул код входа.");

            using var exchangePayload = await PostPlatformJsonAsync("/api/launcher/auth/exchange", new
            {
                request_id = GetQueryValue(authUri, "request_id"),
                code,
                code_verifier = verifier
            }, ct);
            var session = ParseHandoffSession(exchangePayload.RootElement);
            if (session is null) return new(false, false, "Сайт вернул неполную сессию SubReel.");

            using var validation = await SendAsync(HttpMethod.Get, "/auth/v1/user", null, session.AccessToken, ct);
            if (validation.StatusCode == HttpStatusCode.Unauthorized)
                return new(false, false, "Сессия SubReel истекла. Войди через сайт ещё раз.");
            if (!validation.IsSuccessStatusCode)
                return new(false, false, $"Не удалось проверить аккаунт SubReel (HTTP {(int)validation.StatusCode}).");

            await _gate.WaitAsync(ct);
            try
            {
                await SaveSessionAsync(session, ct);
                _session = session;
            }
            finally { _gate.Release(); }
            return new(true, false, "Вход через сайт выполнен.", session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, false, "Вход отменён.");
        }
        catch (OperationCanceledException)
        {
            return new(false, false, "Время ожидания входа истекло. Запусти его заново в лаунчере.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Browser sign-in request to SubReel platform failed");
            return new(false, false, "Не удалось связаться с сайтом SubReel. Проверь интернет и настройки Platform:WebsiteUrl.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser sign-in failed");
            return new(false, false, "Не удалось завершить вход через сайт SubReel. Попробуй ещё раз.");
        }
        finally
        {
            listener.Stop();
        }
    }

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
                using var validation = await SendAsync(HttpMethod.Get, "/auth/v1/user", null, stored.AccessToken, cancellationToken);
                if (validation.IsSuccessStatusCode)
                {
                    _session = stored;
                    return new(true, false, "Сессия восстановлена.", stored);
                }

                if (validation.StatusCode != HttpStatusCode.Unauthorized)
                    return new(false, false, $"Не удалось проверить сессию SubReel (HTTP {(int)validation.StatusCode}).");
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

    private async Task<JsonDocument> PostPlatformJsonAsync(string path, object body, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("SupabaseAuth");
        using var request = new HttpRequestMessage(HttpMethod.Post, _websiteUrl + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var payload = await ReadPayloadAsync(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            payload.Dispose();
            throw new HttpRequestException($"SubReel website returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
        return payload;
    }

    private static async Task<LoopbackCallback> ReceiveLoopbackCallbackAsync(TcpListener listener, CancellationToken ct)
    {
        var client = await listener.AcceptTcpClientAsync(ct);
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(requestLine) || requestLine.Length > 2048)
                throw new InvalidDataException("Invalid launcher callback request.");
            for (var headerCount = 0; headerCount < 64; headerCount++)
            {
                var header = await reader.ReadLineAsync(ct);
                if (header is null || header.Length > 8192) throw new InvalidDataException("Invalid launcher callback headers.");
                if (header.Length == 0) break;
            }

            var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            if (parts.Length != 3 || parts[0] != "GET" || !Uri.TryCreate($"http://127.0.0.1:{port}{parts[1]}", UriKind.Absolute, out var callback) || callback.AbsolutePath != "/callback")
                throw new InvalidDataException("Unexpected launcher callback path.");
            return new LoopbackCallback(client, callback);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetQueryValue(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) == name)
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return null;
    }

    private static SubReelAuthSession? ParseHandoffSession(JsonElement root)
    {
        if (!TryString(root, "access_token", out var access) || !TryString(root, "refresh_token", out var refresh) ||
            !root.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt32(out var seconds) ||
            !root.TryGetProperty("user", out var user) || !TryString(user, "id", out var userId))
            return null;
        var email = TryString(user, "email", out var emailValue) ? emailValue : null;
        return new(access, refresh, DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 60, 86400)), userId, email);
    }

    private static async Task WriteCallbackResponseAsync(NetworkStream responseStream, string message, CancellationToken ct)
    {
        await using var response = responseStream;
        var html = $"<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>SubReel Launcher</title><body style=\"font:16px system-ui;background:#0b0b12;color:#eee;display:grid;place-items:center;min-height:90vh\"><main><h1>SubReel Launcher</h1><p>{System.Net.WebUtility.HtmlEncode(message)}</p></main></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {bytes.Length}\r\n\r\n");
        await response.WriteAsync(header, ct);
        await response.WriteAsync(bytes, ct);
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
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            await AtomicFileWriter.WriteAllBytesAsync(_sessionPath, protectedBytes, ct);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
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
            _logger.LogWarning(ex, "Локальная сессия Supabase повреждена или недоступна; сохраняем копию и просим войти повторно");
            QuarantineSessionFile();
            return null;
        }
    }

    private void QuarantineSessionFile()
    {
        try
        {
            if (!File.Exists(_sessionPath)) return;
            var backupPath = $"{_sessionPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
            File.Move(_sessionPath, backupPath);
            _logger.LogWarning("Повреждённая сессия сохранена в резервную копию {BackupPath}", backupPath);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Не удалось переместить повреждённую сессию в резервную копию");
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
