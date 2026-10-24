using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace SubReel.Launcher.App.Services;

public sealed class VelopackUpdateService : IUpdateService
{
    private readonly string _feedUrl;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;

    public VelopackUpdateService(string? feedUrl, string? githubRepository = null, bool includePrereleases = false)
    {
        _feedUrl = feedUrl?.Trim() ?? string.Empty;
        var repository = githubRepository?.Trim().TrimEnd('/');
        if (Uri.TryCreate(repository, UriKind.Absolute, out var githubUri) &&
            githubUri.Scheme == Uri.UriSchemeHttps &&
            githubUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            githubUri.AbsolutePath.Trim('/').Split('/').Length == 2)
        {
            _manager = new UpdateManager(new GithubSource(repository, string.Empty, includePrereleases));
            return;
        }

        if (Uri.TryCreate(_feedUrl, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || uri.IsFile))
        {
            _manager = new UpdateManager(_feedUrl);
        }
    }

    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString() ??
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "неизвестно";

    public string ConfigurationStatus => _manager is null
        ? "Источник обновлений не настроен. Укажи Updates:FeedUrl после публикации релизов."
        : _manager?.IsInstalled == true
            ? "Автообновление готово."
            : "Обновления доступны в установленной версии лаунчера.";

    public bool CanCheckForUpdates => _manager?.IsInstalled == true;

    public async Task<string?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_manager is null)
            throw new InvalidOperationException("Источник обновлений не настроен.");
        if (!_manager.IsInstalled)
            throw new InvalidOperationException("Проверка обновлений работает после установки лаунчера через Velopack.");

        _pendingUpdate = await _manager.CheckForUpdatesAsync();
        return _pendingUpdate?.TargetFullRelease.Version.ToString();
    }

    public async Task DownloadAndRestartAsync(Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_manager is null || _pendingUpdate is null)
            throw new InvalidOperationException("Сначала проверь наличие обновлений.");

        await _manager.DownloadUpdatesAsync(_pendingUpdate, progress, cancellationToken);
        _manager.ApplyUpdatesAndRestart(_pendingUpdate.TargetFullRelease);
    }
}
