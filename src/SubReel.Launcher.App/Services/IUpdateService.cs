namespace SubReel.Launcher.App.Services;

public interface IUpdateService
{
    string CurrentVersion { get; }
    string ConfigurationStatus { get; }
    bool CanCheckForUpdates { get; }
    Task<string?> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task DownloadAndRestartAsync(Action<int>? progress = null, CancellationToken cancellationToken = default);
}
