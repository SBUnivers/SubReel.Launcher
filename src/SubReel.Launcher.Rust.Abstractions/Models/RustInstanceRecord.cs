namespace SubReel.Launcher.Rust.Abstractions.Models;

/// <summary>Launcher-owned metadata for a Rust client instance; contains no executable paths or credentials.</summary>
public sealed record RustInstanceRecord(
    Guid Id,
    string ClientId,
    string Name,
    string ReleaseTag,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastPlayedAt = null,
    string? GameVersion = null);
