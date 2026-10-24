using System.Text.Json;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.Abstractions.Settings;

namespace SubReel.Launcher.Infrastructure.Persistence;

public sealed class JsonSettingsService(IAppPaths paths) : ISettingsService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.SettingsFile)) return new LauncherSettings();
            await using var stream = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            return await JsonSerializer.DeserializeAsync(stream, LauncherSettingsJsonContext.Default.LauncherSettings, cancellationToken)
                   ?? new LauncherSettings();
        }
        catch (JsonException)
        {
            return new LauncherSettings();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SchemaVersion = Math.Max(1, settings.SchemaVersion);
        settings.DefaultMemoryMb = Math.Clamp(settings.DefaultMemoryMb, 512, 65536);
        settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 32);
        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = paths.SettingsFile + ".tmp";
        try
        {
            paths.EnsureCreated();
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, LauncherSettingsJsonContext.Default.LauncherSettings, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, paths.SettingsFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            _gate.Release();
        }
    }
}
