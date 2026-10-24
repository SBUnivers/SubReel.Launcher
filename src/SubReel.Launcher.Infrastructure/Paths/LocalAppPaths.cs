using SubReel.Launcher.Abstractions.Paths;

namespace SubReel.Launcher.Infrastructure.Paths;

public sealed class LocalAppPaths : IAppPaths
{
    public string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SubReel", "Launcher");
    public string Data => Path.Combine(Root, "Data");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string Instances => Path.Combine(Root, "Instances");
    public string Shared => Path.Combine(Root, "shared");
    public string SharedVersions => Path.Combine(Shared, "versions");
    public string SharedLibraries => Path.Combine(Shared, "libraries");
    public string SharedAssets => Path.Combine(Shared, "assets");
    public string SharedRuntime => Path.Combine(Shared, "runtime");
    public string SharedResources => Path.Combine(Shared, "resources");
    public string Auth => Path.Combine(Root, "Auth");
    public string AccountsFile => Path.Combine(Auth, "accounts.json");
    public string LauncherAccountsFile => Path.Combine(Auth, "launcher-accounts.dpapi");
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "Logs");
    public string Plugins => Path.Combine(Root, "plugins");

    public void EnsureCreated()
    {
        foreach (var directory in new[]
        {
            Root, Data, Instances, Shared,
            SharedVersions, SharedLibraries, SharedAssets, SharedRuntime, SharedResources,
            Auth, Cache, Path.Combine(Cache, "images"), Logs, Plugins
        }) Directory.CreateDirectory(directory);
    }
}
