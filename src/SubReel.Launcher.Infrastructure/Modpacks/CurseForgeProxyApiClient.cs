using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SubReel.Launcher.Abstractions.Modpacks;

namespace SubReel.Launcher.Infrastructure.Modpacks;

/// <summary>Calls SubReel's authenticated CurseForge proxy; the third-party API key never enters the launcher.</summary>
public sealed class CurseForgeProxyApiClient(HttpClient http, ILauncherSessionProvider session) : ICurseForgeApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CurseForgeSearchResults> SearchAsync(ModpackQuery query, CancellationToken cancellationToken = default,
        bool searchMods = false)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["action"] = "search", ["query"] = query.Query, ["gameVersion"] = query.MinecraftVersion,
            ["type"] = searchMods ? "mod" : "modpack",
            ["loader"] = query.Loader, ["offset"] = query.Offset.ToString(), ["limit"] = query.Limit.ToString()
        };
        using var document = await SendJsonAsync(HttpMethod.Get, BuildPath(parameters), null, cancellationToken);
        var root = document.RootElement;
        var items = root.GetProperty("data").EnumerateArray().Select(ParseProject).ToArray();
        var total = root.TryGetProperty("pagination", out var pagination) && pagination.TryGetProperty("totalCount", out var count) && count.TryGetInt32(out var value)
            ? value : items.Length;
        return new CurseForgeSearchResults(items, total);
    }

    public async Task<IReadOnlyList<CurseForgeModpackFile>> GetFilesAsync(int projectId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(HttpMethod.Get, BuildPath(new Dictionary<string, string?>
        {
            ["action"] = "versions", ["projectId"] = projectId.ToString(), ["offset"] = offset.ToString(), ["limit"] = limit.ToString()
        }), null, cancellationToken);
        return document.RootElement.GetProperty("data").EnumerateArray().Select(ParseFile).ToArray();
    }

    public async Task<IReadOnlyList<CurseForgeModpackFile>> GetFilesByIdsAsync(IReadOnlyList<int> fileIds, CancellationToken cancellationToken = default)
    {
        var output = new List<CurseForgeModpackFile>();
        foreach (var chunk in fileIds.Distinct().Chunk(50))
        {
            using var document = await SendJsonAsync(HttpMethod.Post, "?action=batch-files", new { fileIds = chunk }, cancellationToken);
            output.AddRange(document.RootElement.GetProperty("data").EnumerateArray().Select(ParseFile));
        }
        return output;
    }

    public async Task DownloadFileAsync(int projectId, int fileId, Stream destination, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(HttpMethod.Get, BuildPath(new Dictionary<string, string?>
        {
            ["action"] = "download", ["projectId"] = projectId.ToString(), ["fileId"] = fileId.ToString()
        }), null, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[128 * 1024];
        long received = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (received + read > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Файл CurseForge превышает ограничение размера 2 ГБ.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(received);
        }
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(method, path, body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var accessToken = await session.GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Войди в аккаунт SubReel, чтобы открыть каталог CurseForge.");

        var response = await SendOnceAsync(accessToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized) return response;
        response.Dispose();

        accessToken = await session.GetAccessTokenAsync(cancellationToken, forceRefresh: true);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new HttpRequestException("Сессия SubReel истекла. Войди в аккаунт снова.", null, System.Net.HttpStatusCode.Unauthorized);
        return await SendOnceAsync(accessToken);

        async Task<HttpResponseMessage> SendOnceAsync(string token)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return await http.SendAsync(request, completion, cancellationToken);
        }
    }

    private static string BuildPath(IReadOnlyDictionary<string, string?> parameters) =>
        "?" + string.Join("&", parameters.Where(x => x.Value is not null)
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var trimmedBody = body.AsSpan().TrimStart();
        var isHtml = contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true ||
            trimmedBody.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
            trimmedBody.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound &&
            isHtml)
            throw new HttpRequestException("API-прокси CurseForge ещё не развернут на сайте SubReel. Нужно обновить production-деплой проекта subreel-platform.", null, response.StatusCode);

        if (isHtml)
            body = "Сайт вернул HTML вместо ответа API. Проверьте адрес и production-деплой прокси.";
        else if (body.Length > 500)
            body = body[..500] + "…";
        var serverMessage = TryReadErrorMessage(body);
        var message = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => serverMessage ?? "Сессия SubReel истекла. Войди в аккаунт снова.",
            System.Net.HttpStatusCode.Forbidden => "У аккаунта нет доступа к каталогу CurseForge.",
            System.Net.HttpStatusCode.TooManyRequests => "Слишком много запросов. Подожди минуту и повтори.",
            System.Net.HttpStatusCode.ServiceUnavailable => serverMessage ?? "Каталог CurseForge не настроен на сервере SubReel.",
            _ => $"Запрос к прокси CurseForge не выполнен (HTTP {(int)response.StatusCode}): {serverMessage ?? body}"
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static string? TryReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static ModpackSummary ParseProject(JsonElement project)
    {
        var categories = project.TryGetProperty("categories", out var categoryArray) && categoryArray.ValueKind == JsonValueKind.Array
            ? categoryArray.EnumerateArray().Select(x => ReadString(x, "name")).Where(x => x.Length > 0).ToArray()
            : [];
        var logo = project.TryGetProperty("logo", out var logoObject) ? ReadNullableString(logoObject, "url") : null;
        var author = project.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array
            ? authors.EnumerateArray().Select(item => ReadNullableString(item, "name")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;
        return new ModpackSummary(ReadString(project, "id"), ReadString(project, "slug"), ReadString(project, "name"),
            ReadString(project, "summary"), logo, ReadLong(project, "downloadCount"), categories, ModpackProvider.CurseForge)
        {
            Author = author
        };
    }

    private static CurseForgeModpackFile ParseFile(JsonElement file)
    {
        var hashes = file.TryGetProperty("hashes", out var hashArray) && hashArray.ValueKind == JsonValueKind.Array
            ? hashArray.EnumerateArray().Select(x => new CurseForgeFileHash(ReadString(x, "value"), ReadInt(x, "algo"))).ToArray()
            : [];
        var gameVersions = file.TryGetProperty("gameVersions", out var gameArray) && gameArray.ValueKind == JsonValueKind.Array
            ? gameArray.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
        var dependencies = file.TryGetProperty("dependencies", out var dependencyArray) && dependencyArray.ValueKind == JsonValueKind.Array
            ? dependencyArray.EnumerateArray().Select(x => new CurseForgeFileDependency(ReadInt(x, "modId"), ReadInt(x, "fileId"), ReadInt(x, "relationType")))
                .Where(x => x.ModId > 0).ToArray()
            : [];
        return new CurseForgeModpackFile(ReadInt(file, "id"), ReadInt(file, "modId"), ReadString(file, "displayName"),
            ReadString(file, "fileName"), ReadNullableString(file, "downloadUrl"), ReadLong(file, "fileLength"), hashes,
            gameVersions, !file.TryGetProperty("isAvailable", out var available) || available.ValueKind != JsonValueKind.False)
        {
            Dependencies = dependencies,
            PublishedAt = DateTimeOffset.TryParse(ReadNullableString(file, "fileDate"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var publishedAt) ? publishedAt : null
        };
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";
    private static string? ReadNullableString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int ReadInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
    private static long ReadLong(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;
}
