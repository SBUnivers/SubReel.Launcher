namespace SubReel.Launcher.Abstractions.Modpacks;

public enum ModpackProvider
{
    Modrinth,
    CurseForge
}

public sealed record ModpackQuery(
    string Query = "",
    string? MinecraftVersion = null,
    string? Loader = null,
    int Offset = 0,
    int Limit = 20);

public sealed record InstanceModSearchResult(string Id, string Slug, string Title, string Description,
    string? IconUrl, long Downloads, IReadOnlyList<string> Categories)
{
    public ModpackProvider Provider { get; init; } = ModpackProvider.Modrinth;
}


public sealed record ModpackSummary(
    string Id,
    string Slug,
    string Title,
    string Description,
    string? IconUrl,
    long Downloads,
    IReadOnlyList<string> Categories,
    ModpackProvider Provider)
{
    public string? Author { get; init; }
    public string CategoriesLabel => string.Join(" · ", Categories.Take(3));
    public string DownloadsLabel => Downloads.ToString("N0");
    public string ProviderLabel => Provider switch
    {
        ModpackProvider.CurseForge => "CurseForge",
        _ => "Modrinth"
    };
}

public sealed record ModpackSearchPage(
    IReadOnlyList<ModpackSummary> Items,
    int TotalHits,
    int Offset,
    int Limit);

public sealed record ModpackVersionSummary(
    string Id,
    string Name,
    string VersionNumber,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    string? FileName,
    string? DownloadUrl,
    string? Sha1)
{
    public bool IsInstalled { get; init; }
    public bool IsUpdateAvailable { get; init; }
    public string? InstalledInstanceName { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string InstallButtonLabel => IsInstalled ? "Установлено" : IsUpdateAvailable ? "Установить обновление" : "Установить";
    public bool InstallActionEnabled => CanInstall && !IsInstalled;
    public string CompatibilityLabel =>
        $"Minecraft: {string.Join(", ", GameVersions.Take(4))}  ·  {string.Join(", ", Loaders)}";
    public bool CanInstall => !string.IsNullOrWhiteSpace(DownloadUrl) &&
        (FileName?.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) == true || FileName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
}

public sealed record ModpackInstallProgress(string Message, double Fraction);
public sealed record InstalledModpack(Guid InstanceId, string Name, string MinecraftVersion, string Loader, string? Warning = null);
public sealed record CurseForgeModpackFile(int Id, int ProjectId, string DisplayName, string FileName, string? DownloadUrl,
    long FileLength, IReadOnlyList<CurseForgeFileHash> Hashes, IReadOnlyList<string> GameVersions, bool IsAvailable)
{
    public DateTimeOffset? PublishedAt { get; init; }
    public IReadOnlyList<CurseForgeFileDependency> Dependencies { get; init; } = [];
}
public sealed record CurseForgeFileHash(string Value, int Algorithm);
public sealed record CurseForgeFileDependency(int ModId, int FileId, int RelationType);
public sealed record CurseForgeSearchResults(IReadOnlyList<ModpackSummary> Items, int TotalHits);

/// <summary>Search contract for modpack providers; implementations are registered as modules.</summary>
public interface IModpackCatalog
{
    string Id { get; }
    string DisplayName { get; }
    ModpackProvider Provider { get; }
    Task<ModpackSearchPage> SearchAsync(ModpackQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModpackVersionSummary>> GetVersionsAsync(string projectId, CancellationToken cancellationToken = default);
}

public interface IModpackInstaller
{
    Task<InstalledModpack> InstallAsync(
        ModpackSummary project,
        ModpackVersionSummary version,
        int memoryMb = 4096,
        IProgress<ModpackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ICurseForgeModpackInstaller
{
    Task<InstalledModpack> InstallAsync(ModpackSummary project, ModpackVersionSummary version, int memoryMb = 4096,
        IProgress<ModpackInstallProgress>? progress = null, CancellationToken cancellationToken = default);
}

public interface IModpackTextTranslator
{
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken cancellationToken = default);
}

public interface ITranslationKeyStore
{
    Task<string?> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string? key, CancellationToken cancellationToken = default);
}

public interface ILauncherSessionProvider
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default, bool forceRefresh = false);
}

public interface ICurseForgeApiClient
{
    Task<CurseForgeSearchResults> SearchAsync(ModpackQuery query, CancellationToken cancellationToken = default,
        bool searchMods = false);
    Task<IReadOnlyList<CurseForgeModpackFile>> GetFilesAsync(int projectId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CurseForgeModpackFile>> GetFilesByIdsAsync(IReadOnlyList<int> fileIds, CancellationToken cancellationToken = default);
    Task DownloadFileAsync(int projectId, int fileId, Stream destination, IProgress<long>? progress = null, CancellationToken cancellationToken = default);
}
