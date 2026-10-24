using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using SubReel.Launcher.Abstractions.Game;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Accounts.JsonStorage;
using XboxAuthNet.Game.Msal;
using XboxAuthNet.Game.Msal.OAuth;

namespace SubReel.Launcher.Auth;

/// <summary>Microsoft/Xbox/Minecraft sign-in plus encrypted launcher account metadata.</summary>
[SupportedOSPlatform("windows")]
public sealed class MicrosoftGameAuthService : IGameAuthService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SubReel.Launcher.Accounts.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string _clientId;
    private readonly string _accountStorePath;
    private readonly string _launcherAccountsPath;
    private readonly string _skinCachePath;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<LauncherAccount> _accounts;
    private Guid? _activeAccountId;
    private JELoginHandler? _handler;
    private GameSession? _microsoftSession;

    public MicrosoftGameAuthService(string? clientId, string accountStorePath, string launcherAccountsPath,
        string skinCachePath, IHttpClientFactory httpClientFactory)
    {
        _clientId = clientId?.Trim() ?? string.Empty;
        _accountStorePath = accountStorePath;
        _launcherAccountsPath = launcherAccountsPath;
        _skinCachePath = skinCachePath;
        _httpClientFactory = httpClientFactory;
        (_accounts, _activeAccountId) = ReadAccounts(launcherAccountsPath);
    }

    public bool IsConfigured => Guid.TryParse(_clientId, out _);
    public string MicrosoftClientId => _clientId;
    public event EventHandler? ConfigurationChanged;

    public void ConfigureMicrosoftClientId(string? clientId)
    {
        var normalized = clientId?.Trim() ?? string.Empty;
        if (normalized.Length > 0 && !Guid.TryParse(normalized, out _))
            throw new ArgumentException("Client ID должен быть GUID или пустым.", nameof(clientId));
        if (string.Equals(_clientId, normalized, StringComparison.OrdinalIgnoreCase)) return;
        _clientId = normalized;
        _handler = null;
        _microsoftSession = null;
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
    public LauncherAccount? ActiveAccount => _accounts.FirstOrDefault(account => account.Id == _activeAccountId);
    public IReadOnlyList<LauncherAccount> Accounts => _accounts.ToArray();
    public GameSession? CurrentSession => ActiveAccount?.Type == LauncherAccountType.Microsoft ? _microsoftSession : null;

    public async Task<GameSession?> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var handler = GetHandler();
            if (handler.AccountManager.GetDefaultAccount() is null) return null;
            try
            {
                var session = await LoadProfileAsync(Map(await handler.AuthenticateSilently(cancellationToken)), cancellationToken);
                _microsoftSession = session;
                var account = UpsertMicrosoftAccount(session);
                if (_activeAccountId is null) _activeAccountId = account.Id;
                await SaveAccountsAsync(cancellationToken);
                return session;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                _microsoftSession = null;
                return null;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<GameSession?> GetActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        var active = ActiveAccount;
        if (active?.Type != LauncherAccountType.Microsoft) return null;
        EnsureConfigured();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await LoadProfileAsync(Map(await GetHandler().AuthenticateSilently(cancellationToken)), cancellationToken);
            if (!NormalizeUuid(session.Uuid).Equals(NormalizeUuid(active.Uuid), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Сохранённый Microsoft-аккаунт не совпадает с восстановленным. Войди в нужный аккаунт ещё раз.");
            _microsoftSession = session;
            UpsertMicrosoftAccount(session);
            await SaveAccountsAsync(cancellationToken);
            return session;
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("лицензия Minecraft Java", StringComparison.OrdinalIgnoreCase))
        {
            _microsoftSession = null;
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _microsoftSession = null;
            throw new InvalidOperationException("Не удалось обновить вход Microsoft. Войди в аккаунт заново.", exception);
        }
        finally { _gate.Release(); }
    }

    public async Task<GameSession> SignInAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await LoadProfileAsync(Map(await GetHandler().AuthenticateInteractively(cancellationToken)), cancellationToken);
            _microsoftSession = session;
            var account = UpsertMicrosoftAccount(session);
            _activeAccountId = account.Id;
            await SaveAccountsAsync(cancellationToken);
            return session;
        }
        finally { _gate.Release(); }
    }

    public async Task<LauncherAccount> AddOfflineAccountAsync(string username, CancellationToken cancellationToken = default)
    {
        username = username?.Trim() ?? string.Empty;
        if (username.Length is < 3 or > 16 || username.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new ArgumentException("Ник офлайн-аккаунта: 3–16 символов, только латинские буквы, цифры и _.", nameof(username));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var account = _accounts.FirstOrDefault(item => item.Type == LauncherAccountType.Offline &&
                item.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                account = new LauncherAccount(Guid.NewGuid(), LauncherAccountType.Offline, username, CreateOfflineUuid(username));
                _accounts.Add(account);
            }
            _activeAccountId = account.Id;
            await SaveAccountsAsync(cancellationToken);
            return account;
        }
        finally { _gate.Release(); }
    }

    public async Task ActivateAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var account = _accounts.FirstOrDefault(item => item.Id == accountId)
                ?? throw new InvalidOperationException("Аккаунт не найден.");
            if (account.Type == LauncherAccountType.Microsoft &&
                (_microsoftSession is null || !NormalizeUuid(_microsoftSession.Uuid).Equals(NormalizeUuid(account.Uuid), StringComparison.OrdinalIgnoreCase)))
            {
                EnsureConfigured();
                var session = await LoadProfileAsync(Map(await GetHandler().AuthenticateSilently(cancellationToken)), cancellationToken);
                if (!NormalizeUuid(session.Uuid).Equals(NormalizeUuid(account.Uuid), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Сохранённый Microsoft-аккаунт не совпадает с восстановленным. Войди в нужный аккаунт ещё раз.");
                _microsoftSession = session;
            }
            _activeAccountId = accountId;
            await SaveAccountsAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var account = _accounts.FirstOrDefault(item => item.Id == accountId);
            if (account is null) return;
            if (account.Type == LauncherAccountType.Microsoft)
            {
                if (_handler is not null) await _handler.Signout(cancellationToken);
                _microsoftSession = null;
            }
            _accounts.Remove(account);
            if (_activeAccountId == accountId)
                _activeAccountId = _accounts.FirstOrDefault(item => item.Type == LauncherAccountType.Offline)?.Id;
            await SaveAccountsAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var active = ActiveAccount;
        if (active is null) return;
        await RemoveAccountAsync(active.Id, cancellationToken);
    }

    private LauncherAccount UpsertMicrosoftAccount(GameSession session)
    {
        var uuid = NormalizeUuid(session.Uuid);
        var account = _accounts.FirstOrDefault(item => item.Type == LauncherAccountType.Microsoft &&
            NormalizeUuid(item.Uuid).Equals(uuid, StringComparison.OrdinalIgnoreCase));
        if (account is not null)
        {
            var updated = account with { Username = session.Username, Uuid = uuid, SkinUrl = session.SkinUrl, SkinPath = session.SkinPath, SkinVariant = session.SkinVariant };
            _accounts[_accounts.IndexOf(account)] = updated;
            return updated;
        }
        account = new LauncherAccount(Guid.NewGuid(), LauncherAccountType.Microsoft, session.Username, uuid, session.SkinUrl, session.SkinPath, session.SkinVariant);
        _accounts.Add(account);
        return account;
    }

    private JELoginHandler GetHandler()
    {
        EnsureConfigured();
        if (_handler is not null) return _handler;
        Directory.CreateDirectory(Path.GetDirectoryName(_accountStorePath)!);
        var msal = MsalClientHelper.BuildApplicationWithCache(_clientId).GetAwaiter().GetResult();
        var accountManager = new JsonXboxGameAccountManager(
            new DpapiJsonStorage(_accountStorePath),
            sessionStorage => new XboxGameAccount(sessionStorage),
            JsonSerializerOptions.Default);
        _handler = new JELoginHandlerBuilder().WithOAuthProvider(new MsalCodeFlowProvider(msal))
            .WithAccountManager(accountManager).Build();
        return _handler;
    }

    private async Task SaveAccountsAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_launcherAccountsPath)!;
        Directory.CreateDirectory(directory);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new AccountFile(_activeAccountId, _accounts), JsonOptions);
        var encrypted = ProtectedData.Protect(payload, Entropy, DataProtectionScope.CurrentUser);
        var temporaryPath = _launcherAccountsPath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken);
            File.Move(temporaryPath, _launcherAccountsPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static (List<LauncherAccount> Accounts, Guid? ActiveId) ReadAccounts(string path)
    {
        if (!File.Exists(path)) return ([], null);
        try
        {
            var encrypted = File.ReadAllBytes(path);
            var payload = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var data = JsonSerializer.Deserialize<AccountFile>(payload, JsonOptions);
            return (data?.Accounts ?? [], data?.ActiveId);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException)
        {
            throw new InvalidDataException("Не удалось прочитать защищённый список аккаунтов. Файл не изменён.", exception);
        }
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured) throw new InvalidOperationException("Укажи Microsoft Client ID в разделе «Настройки».");
    }

    private static string CreateOfflineUuid(string username)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{username}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeUuid(string uuid) => uuid.Replace("-", string.Empty, StringComparison.Ordinal);

    private async Task<GameSession> LoadProfileAsync(GameSession session, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var response = await _httpClientFactory.CreateClient("Minecraft").SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("У этого Microsoft-аккаунта не найдена лицензия Minecraft Java.");
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Сессия Minecraft истекла. Войди через Microsoft ещё раз.");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var username = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
        var uuid = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        string? skinUrl = null;
        string? variant = null;
        if (root.TryGetProperty("skins", out var skins) && skins.ValueKind == JsonValueKind.Array)
        {
            var skin = skins.EnumerateArray().FirstOrDefault(item =>
                item.TryGetProperty("state", out var state) && state.GetString()?.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) == true);
            if (skin.ValueKind == JsonValueKind.Object)
            {
                skinUrl = skin.TryGetProperty("url", out var url) ? url.GetString() : null;
                variant = skin.TryGetProperty("variant", out var model) ? model.GetString() : null;
            }
        }

        var profileSession = session with
        {
            Username = string.IsNullOrWhiteSpace(username) ? session.Username : username,
            Uuid = string.IsNullOrWhiteSpace(uuid) ? session.Uuid : uuid,
            SkinUrl = skinUrl,
            SkinVariant = variant
        };
        if (string.IsNullOrWhiteSpace(skinUrl)) return profileSession;
        string? skinPath;
        try { skinPath = await CacheSkinAsync(profileSession.Uuid, skinUrl, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            // The account remains usable when the optional skin CDN is unavailable.
            skinPath = null;
        }
        return profileSession with { SkinPath = skinPath };
    }

    private async Task<string?> CacheSkinAsync(string uuid, string skinUrl, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_skinCachePath);
        var path = Path.Combine(_skinCachePath, NormalizeUuid(uuid) + ".png");
        if (File.Exists(path) && new FileInfo(path).Length <= 2_000_000 && IsMinecraftSkinPng(await File.ReadAllBytesAsync(path, cancellationToken))) return path;
        if (!Uri.TryCreate(skinUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("textures.minecraft.net", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || (uri.Port != 80 && uri.Port != 443))
            return null;
        uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        using var response = await _httpClientFactory.CreateClient("Minecraft").GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2_000_000)
            throw new InvalidDataException("Файл скина превышает допустимый размер.");
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var skinBytes = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await responseStream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (skinBytes.Length + read > 2_000_000)
                throw new InvalidDataException("Файл скина превышает допустимый размер.");
            skinBytes.Write(buffer, 0, read);
        }
        var bytes = skinBytes.ToArray();
        if (!IsMinecraftSkinPng(bytes)) throw new InvalidDataException("Сервер вернул некорректное изображение скина.");
        var temporaryPath = path + ".part";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        return path;
    }

    private static bool IsMinecraftSkinPng(byte[] bytes)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        return width == 64 && height is 64 or 32;
    }

    private static GameSession Map(MSession session) => new(
        session.Username ?? throw new InvalidOperationException("Minecraft не вернул имя профиля."),
        session.UUID ?? throw new InvalidOperationException("Minecraft не вернул UUID профиля."),
        session.AccessToken ?? throw new InvalidOperationException("Minecraft не вернул токен сессии."),
        session.Xuid);

    private sealed record AccountFile(Guid? ActiveId, List<LauncherAccount> Accounts);
}

[SupportedOSPlatform("windows")]
internal sealed class DpapiJsonStorage(string filePath) : IJsonStorage
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SubReel.Launcher.MicrosoftTokens.v1");

    public System.Text.Json.Nodes.JsonNode ReadAsJsonNode()
    {
        if (!File.Exists(filePath)) return new System.Text.Json.Nodes.JsonObject();
        var stored = File.ReadAllBytes(filePath);
        byte[] json;
        try
        {
            json = ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Upgrade the prior JsonXboxGameAccountManager plaintext file once, then keep only DPAPI data.
            json = stored;
        }

        var node = System.Text.Json.Nodes.JsonNode.Parse(json)
            ?? new System.Text.Json.Nodes.JsonObject();
        if (ReferenceEquals(json, stored)) Write(node, JsonSerializerOptions.Default);
        return node;
    }

    public void Write(System.Text.Json.Nodes.JsonNode value, JsonSerializerOptions? options)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, options ?? JsonSerializerOptions.Default);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = filePath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
