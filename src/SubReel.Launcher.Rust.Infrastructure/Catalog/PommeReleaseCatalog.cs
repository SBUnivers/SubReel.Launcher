using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using SubReel.Launcher.Rust.Abstractions.Models;
using SubReel.Launcher.Rust.Abstractions.Services;

namespace SubReel.Launcher.Rust.Infrastructure.Catalog;

/// <summary>Reads Pomme's published Windows x64 artifacts from the official GitHub Releases API.</summary>
public sealed class PommeReleaseCatalog(HttpClient httpClient) : IRustClientCatalog
{
    public const string ClientId = "pomme";
    private static readonly Uri RepositoryUri = new("https://github.com/PommeMC/Client");
    private static readonly Uri WebsiteUri = new("https://pomme.rs/");
    private static readonly RustClientDescriptor Pomme = new(
        ClientId,
        "Pomme",
        RepositoryUri,
        WebsiteUri,
        "GPL-3.0-or-later",
        "Нативный клиент на Rust. Не запускает Java-моды; вход Microsoft выполняется самим клиентом.");

    public IReadOnlyList<RustClientDescriptor> GetClients() => [Pomme];

    public async Task<IReadOnlyList<RustClientRelease>> GetReleasesAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(clientId, ClientId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Неизвестный Rust-клиент '{clientId}'.", nameof(clientId));

        using var response = await httpClient.GetAsync(
            "repos/PommeMC/Client/releases?per_page=50", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub вернул неожиданный формат каталога Pomme.");

        var releases = new List<RustClientRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var tag = GetString(item, "tag_name");
            var htmlUrl = GetString(item, "html_url");
            if (string.IsNullOrWhiteSpace(tag) || !TryOfficialReleaseUri(htmlUrl, out var releaseUri)) continue;
            if (!DateTimeOffset.TryParse(GetString(item, "published_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var publishedAt))
                continue;

            var assets = new List<RustClientAsset>();
            if (item.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetArray.EnumerateArray())
                {
                    var name = GetString(asset, "name");
                    if (string.IsNullOrWhiteSpace(name) ||
                        !name.StartsWith("pomme-client-windows-x64", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!TryOfficialAssetUri(GetString(asset, "browser_download_url"), out var downloadUri)) continue;
                    var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes)
                        ? bytes
                        : 0;
                    var digest = GetString(asset, "digest");
                    var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest[7..]
                        : null;
                    assets.Add(new RustClientAsset(name, downloadUri, size, IsSha256(sha256) ? sha256 : null));
                }
            }

            releases.Add(new RustClientRelease(ClientId, tag, tag, publishedAt,
                item.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True,
                releaseUri, assets));
        }

        return releases.OrderByDescending(x => x.PublishedAt).ToArray();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryOfficialReleaseUri(string? value, out Uri uri) =>
        TryOfficialUri(value, "/PommeMC/Client/releases/tag/", out uri);

    private static bool TryOfficialAssetUri(string? value, out Uri uri) =>
        TryOfficialUri(value, "/PommeMC/Client/releases/download/", out uri);

    private static bool TryOfficialUri(string? value, string pathPrefix, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps &&
            parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            parsed.AbsolutePath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
