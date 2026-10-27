using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace SubReel.Launcher.App.Services;

public sealed record LauncherRelease(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("news_tag")] string NewsTag,
    [property: JsonPropertyName("cover_url")] string? CoverUrl,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("change_notes")] string[] ChangeNotes,
    [property: JsonPropertyName("github_release_url")] string GitHubReleaseUrl,
    [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt)
{
    public bool IsNew => PublishedAt >= DateTimeOffset.UtcNow.AddDays(-7);
    public string DateLabel
    {
        get
        {
            var cultureName = System.Windows.Application.Current?.TryFindResource("News.DateCulture") as string ?? "ru-RU";
            var format = System.Windows.Application.Current?.TryFindResource("News.DateFormat") as string ?? "dd.MM.yyyy";
            return PublishedAt.ToLocalTime().ToString(format, System.Globalization.CultureInfo.GetCultureInfo(cultureName));
        }
    }

    public string ChannelLabel => System.Windows.Application.Current?.TryFindResource(
        Channel.Equals("beta", StringComparison.OrdinalIgnoreCase) ? "News.Channel.Beta" : "News.Channel.Release") as string
        ?? (Channel.Equals("beta", StringComparison.OrdinalIgnoreCase) ? "Бета лаунчера" : "Релиз лаунчера");

    public string NewsTagLabel
    {
        get
        {
            var key = NewsTag switch { "server" => "News.Tag.Server", "event" => "News.Tag.Event", _ => "News.Tag.Launcher" };
            var fallback = NewsTag switch { "server" => "Сервер", "event" => "Событие", _ => "Лаунчер" };
            return System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;
        }
    }
}

public interface ILauncherNewsService
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<LauncherRelease>> GetLatestAsync(CancellationToken cancellationToken = default);
}

public sealed class PlatformLauncherNewsService : ILauncherNewsService
{
    private readonly HttpClient _client;
    private readonly string _apiBaseUrl;
    public bool IsConfigured => Uri.TryCreate(_apiBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public PlatformLauncherNewsService(HttpClient client, IConfiguration configuration)
    {
        _client = client;
        _apiBaseUrl = (configuration["Platform:ApiBaseUrl"] ?? "https://www.subreel.ru/api/launcher/").TrimEnd('/') + "/";
    }

    public async Task<IReadOnlyList<LauncherRelease>> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("Адрес SubReel Platform не настроен.");
        using var response = await _client.GetAsync(new Uri(new Uri(_apiBaseUrl), "releases"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<List<LauncherRelease>>(stream, cancellationToken: cancellationToken)
            ?? [];
    }
}
