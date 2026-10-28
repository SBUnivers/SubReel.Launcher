namespace SubReel.Launcher.Rust.Abstractions.Models;

public sealed record RustClientDescriptor(
    string Id,
    string Name,
    Uri RepositoryUri,
    Uri WebsiteUri,
    string License,
    string Summary);
