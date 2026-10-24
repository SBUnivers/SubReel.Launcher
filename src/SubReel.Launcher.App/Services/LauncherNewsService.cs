using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace SubReel.Launcher.App.Services;

public sealed record LauncherRelease(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("change_notes")] string[] ChangeNotes,
    [property: JsonPropertyName("github_release_url")] string GitHubReleaseUrl,
    [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt)
{
    public bool IsNew => PublishedAt >= DateTimeOffset.UtcNow.AddDays(-7);
    public string DateLabel => PublishedAt.ToLocalTime().ToString("dd.MM.yyyy");
    public string ChannelLabel => Channel.Equals("beta", StringComparison.OrdinalIgnoreCase) ? "Бета лаунчера" : "Релиз лаунчера";
}

public interface ILauncherNewsService
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<LauncherRelease>> GetLatestAsync(CancellationToken cancellationToken = default);
}

public sealed class SupabaseLauncherNewsService : ILauncherNewsService
{
    private readonly HttpClient _client;
    private readonly string _url;
    private readonly string _key;
    public bool IsConfigured => Uri.TryCreate(_url, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(_key);

    public SupabaseLauncherNewsService(HttpClient client, IConfiguration configuration)
    {
        _client = client;
        _url = configuration["Platform:SupabaseUrl"]?.TrimEnd('/') ?? "";
        _key = configuration["Platform:SupabasePublishableKey"] ?? "";
    }

    public async Task<IReadOnlyList<LauncherRelease>> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return Array.Empty<LauncherRelease>();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_url}/rest/v1/launcher_releases?select=id,version,channel,title,summary,change_notes,github_release_url,published_at&status=eq.published&order=published_at.desc&limit=30");
        request.Headers.TryAddWithoutValidation("apikey", _key);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_key}");
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<List<LauncherRelease>>(stream, cancellationToken: cancellationToken)
            ?? new List<LauncherRelease>();
    }
}
