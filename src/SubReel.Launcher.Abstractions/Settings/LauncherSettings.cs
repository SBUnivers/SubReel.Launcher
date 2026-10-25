namespace SubReel.Launcher.Abstractions.Settings;

/// <summary>User-editable settings persisted in the per-user settings.json file.</summary>
public sealed class LauncherSettings
{
    public int SchemaVersion { get; set; } = 1;
    public int DefaultMemoryMb { get; set; } = 4096;
    public int MaxConcurrentDownloads { get; set; } = 8;
    public string? JavaPath { get; set; }
    public string AccentColor { get; set; } = "#8875FF";
    public bool ReduceAnimations { get; set; }
    public string WindowBackdrop { get; set; } = "Mica";
    public string Language { get; set; } = "ru-RU";
    public string? LastSeenNewsId { get; set; }
    public string? LastSelectedVersionGroup { get; set; }
    public string? LastSelectedMinecraftVersion { get; set; }
    public string LastSelectedLoader { get; set; } = "Vanilla";
    public bool IncludeSnapshots { get; set; }
    public bool HasCompletedWelcomeSetup { get; set; }
}

public interface ISettingsService
{
    Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default);
}
