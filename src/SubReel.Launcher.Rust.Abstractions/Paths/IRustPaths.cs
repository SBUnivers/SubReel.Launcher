namespace SubReel.Launcher.Rust.Abstractions.Paths;

public interface IRustPaths
{
    string Root { get; }
    string Clients { get; }
    string Instances { get; }
    string InstanceIndexFile { get; }
    string InstalledClientsFile { get; }
    string GetClientReleaseDirectory(string clientId, string releaseTag);
    string GetInstanceDirectory(Guid instanceId);
    void EnsureCreated();
}
