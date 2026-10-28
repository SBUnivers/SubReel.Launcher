namespace SubReel.Launcher.Rust.Abstractions.Models;

public sealed record RustInstalledClient(
    string ClientId,
    string ReleaseTag,
    DateTimeOffset InstalledAt,
    string ArchiveSha256,
    IReadOnlyDictionary<string, string> FileSha256);

public enum RustInstallStage { Downloading, Verifying, Extracting, Registering }

public sealed record RustInstallProgress(
    RustInstallStage Stage,
    long BytesCompleted = 0,
    long BytesTotal = 0,
    string? FileName = null);
