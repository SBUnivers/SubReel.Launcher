using SubReel.Launcher.Rust.Abstractions.Paths;

namespace SubReel.Launcher.Rust.Infrastructure.Paths;

public sealed class RustPaths : IRustPaths
{
    private readonly string _launcherRoot;

    public RustPaths(string launcherRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherRoot);
        _launcherRoot = Path.GetFullPath(launcherRoot);
    }

    public string Root => Path.Combine(_launcherRoot, "Rust");
    public string Clients => Path.Combine(Root, "Clients");
    public string Instances => Path.Combine(Root, "Instances");
    public string InstanceIndexFile => Path.Combine(Root, "instances.json");
    public string InstalledClientsFile => Path.Combine(Root, "installed-clients.json");

    public string GetClientReleaseDirectory(string clientId, string releaseTag) =>
        Path.Combine(Clients, ValidateSegment(clientId, nameof(clientId)), ValidateSegment(releaseTag, nameof(releaseTag)));

    public string GetInstanceDirectory(Guid instanceId)
    {
        if (instanceId == Guid.Empty) throw new ArgumentException("Не задан ID Rust-инстанса.", nameof(instanceId));
        return Path.Combine(Instances, instanceId.ToString("N"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Clients);
        Directory.CreateDirectory(Instances);
    }

    private static string ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 100 || value is "." or ".." ||
            !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '+' or '.'))
            throw new ArgumentException("Значение не может использоваться как имя каталога.", parameterName);
        return value;
    }
}
