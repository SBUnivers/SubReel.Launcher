using System.Diagnostics;

namespace SubReel.Launcher.Abstractions.Game;

/// <summary>Всё, что нужно, чтобы установить и запустить сборку.</summary>
public sealed record GameLaunchRequest(
    string GameDirectory,
    string MinecraftVersion,
    int MemoryMb,
    string PlayerName,
    GameSession? Session = null,
    string LoaderType = "Vanilla",
    string? LoaderVersion = null,
    Guid? InstanceId = null,
    string? JavaPath = null,
    string? JvmArguments = null);

/// <summary>Игровая сессия Minecraft, действующая только после входа Microsoft.</summary>
public sealed record GameSession(string Username, string Uuid, string AccessToken, string? Xuid = null,
    string? SkinUrl = null, string? SkinPath = null, string? SkinVariant = null);

public enum LauncherAccountType { Offline, Microsoft }

/// <summary>Non-secret account metadata shown by the launcher UI.</summary>
public sealed record LauncherAccount(Guid Id, LauncherAccountType Type, string Username, string Uuid,
    string? SkinUrl = null, string? SkinPath = null, string? SkinVariant = null);

public interface IGameAuthService
{
    GameSession? CurrentSession { get; }
    LauncherAccount? ActiveAccount { get; }
    IReadOnlyList<LauncherAccount> Accounts { get; }
    Task<GameSession?> TryRestoreSessionAsync(CancellationToken cancellationToken = default);
    Task<GameSession?> GetActiveSessionAsync(CancellationToken cancellationToken = default);
    Task<GameSession> SignInAsync(CancellationToken cancellationToken = default);
    Task<LauncherAccount> AddOfflineAccountAsync(string username, CancellationToken cancellationToken = default);
    Task ActivateAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>Прогресс установки файлов (0..1 и подпись).</summary>
public sealed record GameInstallProgress(
    string Stage,
    int ProgressedTasks,
    int TotalTasks,
    long ProgressedBytes = 0,
    long TotalBytes = 0)
{
    public double Fraction =>
        TotalBytes > 0 ? Math.Clamp((double)ProgressedBytes / TotalBytes, 0, 1)
            : TotalTasks <= 0 ? 0 : Math.Clamp((double)ProgressedTasks / TotalTasks, 0, 1);
}

/// <summary>
/// Контракт игровой части. Реализация (CmlLib.Core) спрятана за ним,
/// чтобы её можно было заменить, не трогая UI и Core.
/// </summary>
public interface IGameService
{
    /// <summary>Устанавливает необходимые файлы и запускает игру.</summary>
    Task<Process> LaunchAsync(
        GameLaunchRequest request,
        IProgress<GameInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record GameVersionInfo(
    string Id,
    string Type,
    DateTimeOffset? ReleaseTime = null,
    string? ManifestUrl = null,
    string? Sha1 = null);

/// <summary>Каталог публичных версий Minecraft, предоставляемый игровым адаптером.</summary>
public interface IGameVersionCatalog
{
    Task<IReadOnlyList<GameVersionInfo>> GetVersionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>One loader choice available for a specific Minecraft version.</summary>
public sealed record GameLoaderOption(
    string Id,
    string Name,
    string? Version,
    bool IsAvailable,
    string AvailabilityMessage);

/// <summary>Reports loader compatibility and the recommended install version.</summary>
public interface IGameLoaderCatalog
{
    Task<IReadOnlyList<GameLoaderOption>> GetLoadersAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default);
}
