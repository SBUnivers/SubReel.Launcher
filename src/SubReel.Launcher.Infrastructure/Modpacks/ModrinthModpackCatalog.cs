using System.Text.Json;
using SubReel.Launcher.Abstractions.Modpacks;

namespace SubReel.Launcher.Infrastructure.Modpacks;

/// <summary>Modrinth modpack search adapter. Installation is added in the next step.</summary>
public sealed class ModrinthModpackCatalog(HttpClient httpClient) : IModpackCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Id => "modrinth";
    public string DisplayName => "Modrinth";

    public async Task<ModpackSearchPage> SearchAsync(ModpackQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, 100);
        var offset = Math.Max(0, query.Offset);
        var facets = new List<string[]> { new[] { "project_type:modpack" } };
        if (!string.IsNullOrWhiteSpace(query.MinecraftVersion))
            facets.Add(new[] { $"versions:{query.MinecraftVersion.Trim()}" });
        if (!string.IsNullOrWhiteSpace(query.Loader) && !query.Loader.Equals("Any", StringComparison.OrdinalIgnoreCase))
            facets.Add(new[] { $"categories:{query.Loader.Trim().ToLowerInvariant()}" });

        var facetJson = Uri.EscapeDataString(JsonSerializer.Serialize(facets, JsonOptions));
        var searchText = Uri.EscapeDataString(query.Query?.Trim() ?? string.Empty);
        var uri = $"search?query={searchText}&facets={facetJson}&index=downloads&limit={limit}&offset={offset}";
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        var items = new List<ModpackSummary>();
        if (root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hits.EnumerateArray())
            {
                var id = ReadString(hit, "project_id") ?? ReadString(hit, "id");
                var slug = ReadString(hit, "slug");
                var title = ReadString(hit, "title");
                if (id is null || slug is null || title is null) continue;

                var categories = hit.TryGetProperty("categories", out var categoryArray) && categoryArray.ValueKind == JsonValueKind.Array
                    ? categoryArray.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                    : [];
                var downloads = hit.TryGetProperty("downloads", out var count) && count.TryGetInt64(out var value) ? value : 0;
                items.Add(new ModpackSummary(id, slug, title,
                    ReadString(hit, "description") ?? string.Empty,
                    ReadString(hit, "icon_url"), downloads, categories, Id));
            }
        }

        var total = root.TryGetProperty("total_hits", out var totalElement) && totalElement.TryGetInt32(out var totalValue)
            ? totalValue : items.Count;
        return new ModpackSearchPage(items, total, offset, limit);
    }

    public async Task<IReadOnlyList<ModpackVersionSummary>> GetVersionsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("Не указан проект Modrinth.", nameof(projectId));
        var uri = $"project/{Uri.EscapeDataString(projectId.Trim())}/version";
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        var versions = new List<ModpackVersionSummary>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var id = ReadString(item, "id");
            if (id is null) continue;
            var file = default(JsonElement);
            if (item.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                var modpackFiles = files.EnumerateArray()
                    .Where(candidate => ReadString(candidate, "filename")?.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) == true)
                    .ToArray();
                file = modpackFiles.FirstOrDefault(candidate =>
                    candidate.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True);
                if (file.ValueKind == JsonValueKind.Undefined) file = modpackFiles.FirstOrDefault();
            }
            var hashes = file.ValueKind == JsonValueKind.Object && file.TryGetProperty("hashes", out var hashElement)
                ? hashElement : default;
            versions.Add(new ModpackVersionSummary(
                id,
                ReadString(item, "name") ?? "Сборка",
                ReadString(item, "version_number") ?? id,
                ReadStringArray(item, "game_versions"),
                ReadStringArray(item, "loaders"),
                file.ValueKind == JsonValueKind.Object ? ReadString(file, "filename") : null,
                file.ValueKind == JsonValueKind.Object ? ReadString(file, "url") : null,
                hashes.ValueKind == JsonValueKind.Object ? ReadString(hashes, "sha1") : null));
        }
        return versions;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
}
