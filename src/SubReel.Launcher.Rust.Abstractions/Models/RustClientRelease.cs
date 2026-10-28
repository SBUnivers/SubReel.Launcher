namespace SubReel.Launcher.Rust.Abstractions.Models;

public sealed record RustClientAsset(
    string Name,
    Uri DownloadUri,
    long SizeBytes,
    string? Sha256);

public sealed record RustClientRelease(
    string ClientId,
    string Tag,
    string DisplayVersion,
    DateTimeOffset PublishedAt,
    bool IsPrerelease,
    Uri ReleaseNotesUri,
    IReadOnlyList<RustClientAsset> WindowsX64Assets);
