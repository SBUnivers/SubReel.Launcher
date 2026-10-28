namespace SubReel.Launcher.Rust.Abstractions.Models;

/// <summary>Non-secret launch settings. Authentication is intentionally handled separately.</summary>
public sealed record RustLaunchRequest(
    RustInstanceRecord Instance,
    string? Username = null);

public sealed record RustLaunchResult(Guid InstanceId, int ProcessId, DateTimeOffset StartedAt);
