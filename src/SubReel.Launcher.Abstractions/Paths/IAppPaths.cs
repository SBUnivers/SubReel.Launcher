namespace SubReel.Launcher.Abstractions.Paths;

/// <summary>Canonical per-user storage locations for the launcher.</summary>
public interface IAppPaths
{
    string Root { get; }
    string Data { get; }
    string SettingsFile { get; }
    string Instances { get; }
    string Shared { get; }
    string SharedVersions { get; }
    string SharedLibraries { get; }
    string SharedAssets { get; }
    string SharedRuntime { get; }
    string SharedResources { get; }
    string Auth { get; }
    string AccountsFile { get; }
    string LauncherAccountsFile { get; }
    string Cache { get; }
    string Logs { get; }
    string Plugins { get; }
    void EnsureCreated();
}
