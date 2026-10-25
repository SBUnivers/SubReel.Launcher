using System.Collections.Concurrent;
using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Abstractions.Paths;

namespace SubReel.Launcher.Infrastructure.Game;

/// <summary>Queries CmlLib's loader catalogs and caches recommendations for one hour.</summary>
public sealed class CmlLibGameLoaderCatalog(IAppPaths paths, IHttpClientFactory httpClientFactory) : IGameLoaderCatalog
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<GameLoaderOption>> GetLoadersAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("Укажи версию Minecraft.", nameof(minecraftVersion));

        minecraftVersion = minecraftVersion.Trim();
        if (_cache.TryGetValue(minecraftVersion, out var cached) && DateTimeOffset.UtcNow - cached.CreatedAt < CacheLifetime)
            return cached.Options;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(minecraftVersion, out cached) && DateTimeOffset.UtcNow - cached.CreatedAt < CacheLifetime)
                return cached.Options;

            var httpClient = httpClientFactory.CreateClient("LoaderMetadata");
            var launcher = CreateLauncher(httpClient);
            var options = new List<GameLoaderOption>
            {
                new("Vanilla", "Vanilla", null, true, "Доступно для выбранной версии.")
            };

            var loaderOptions = await Task.WhenAll(
                LoadFabricAsync(httpClient, minecraftVersion, cancellationToken),
                LoadQuiltAsync(httpClient, minecraftVersion, cancellationToken),
                LoadNeoForgeAsync(launcher, minecraftVersion, cancellationToken),
                LoadForgeAsync(launcher, minecraftVersion, cancellationToken));
            options.AddRange(loaderOptions);

            _cache[minecraftVersion] = new CacheEntry(DateTimeOffset.UtcNow, options);
            return options;
        }
        finally { _gate.Release(); }
    }

    private GameLoaderOption LoadFabricAsyncResult(string? version, bool stable) => version is null
        ? new GameLoaderOption("Fabric", "Fabric", null, false, "Fabric пока не поддерживает эту версию Minecraft.")
        : new GameLoaderOption("Fabric", "Fabric", version, true, stable ? $"Стабильная версия {version}." : $"Последняя доступная версия {version}.");

    private async Task<GameLoaderOption> LoadFabricAsync(HttpClient httpClient, string minecraftVersion, CancellationToken cancellationToken)
    {
        try
        {
            var versions = await new FabricInstaller(httpClient).GetLoaders(minecraftVersion);
            cancellationToken.ThrowIfCancellationRequested();
            var loader = versions.FirstOrDefault(x => x.Stable) ?? versions.FirstOrDefault();
            return LoadFabricAsyncResult(loader?.Version, loader?.Stable == true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Unavailable("Fabric", "Fabric"); }
    }

    private static async Task<GameLoaderOption> LoadQuiltAsync(HttpClient httpClient, string minecraftVersion, CancellationToken cancellationToken)
    {
        try
        {
            var versions = await new QuiltInstaller(httpClient).GetLoaders(minecraftVersion);
            cancellationToken.ThrowIfCancellationRequested();
            // Quilt's metadata endpoint does not guarantee that its list is ordered by recency.
            // Prefer the newest compatible build; picking the first stable entry can select an
            // old loader whose bundled ASM cannot parse newer Minecraft/Java class files.
            var loader = versions
                .Where(x => !string.IsNullOrWhiteSpace(x.Version))
                .OrderByDescending(x => x.Version!, Comparer<string>.Create(CompareLoaderVersions))
                .FirstOrDefault();
            return loader is null
                ? Unavailable("Quilt", "Quilt")
                : new GameLoaderOption("Quilt", "Quilt", loader.Version, true,
                    loader.Stable ? $"Стабильная версия {loader.Version}." : $"Последняя доступная версия {loader.Version} (предварительная).");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Unavailable("Quilt", "Quilt"); }
    }

    private static async Task<GameLoaderOption> LoadForgeAsync(MinecraftLauncher launcher, string minecraftVersion, CancellationToken cancellationToken)
    {
        try
        {
            var versions = (await new ForgeInstaller(launcher).GetForgeVersions(minecraftVersion)).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var version = versions.FirstOrDefault(x => x.IsRecommendedVersion)
                ?? versions.FirstOrDefault(x => x.IsLatestVersion)
                ?? versions.FirstOrDefault();
            return version is null
                ? Unavailable("Forge", "Forge")
                : new GameLoaderOption("Forge", "Forge", version.ForgeVersionName, true,
                    version.IsRecommendedVersion ? $"Рекомендуемая версия {version.ForgeVersionName}." : $"Доступна версия {version.ForgeVersionName}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Unavailable("Forge", "Forge"); }
    }

    private static async Task<GameLoaderOption> LoadNeoForgeAsync(MinecraftLauncher launcher, string minecraftVersion, CancellationToken cancellationToken)
    {
        try
        {
            var versions = await new NeoForgeInstaller(launcher).GetNeoForgeVersions(minecraftVersion);
            cancellationToken.ThrowIfCancellationRequested();
            var version = versions.FirstOrDefault();
            return version is null
                ? Unavailable("NeoForge", "NeoForge")
                : new GameLoaderOption("NeoForge", "NeoForge", version.NeoForgeVersionName, true,
                    $"Последняя доступная версия {version.NeoForgeVersionName}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Unavailable("NeoForge", "NeoForge"); }
    }

    private MinecraftLauncher CreateLauncher(HttpClient httpClient)
    {
        paths.EnsureCreated();
        var path = new MinecraftPath(Path.GetFullPath(paths.Root))
        {
            Library = Path.GetFullPath(paths.SharedLibraries),
            Versions = Path.GetFullPath(paths.SharedVersions),
            Assets = Path.GetFullPath(paths.SharedAssets),
            Runtime = Path.GetFullPath(paths.SharedRuntime),
            Resource = Path.GetFullPath(paths.SharedResources)
        };
        path.CreateDirs();
        return new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(path, httpClient));
    }

    private static GameLoaderOption Unavailable(string id, string name) =>
        new(id, name, null, false, $"{name} не поддерживает эту версию игры или каталог временно недоступен.");

    private static int CompareLoaderVersions(string left, string right)
    {
        var leftParts = SplitVersion(left);
        var rightParts = SplitVersion(right);
        var coreComparison = leftParts.Core.CompareTo(rightParts.Core);
        if (coreComparison != 0) return coreComparison;
        if (leftParts.Prerelease is null) return rightParts.Prerelease is null ? 0 : 1;
        if (rightParts.Prerelease is null) return -1;

        var leftIdentifiers = leftParts.Prerelease.Split('.');
        var rightIdentifiers = rightParts.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(leftIdentifiers.Length, rightIdentifiers.Length); index++)
        {
            var leftIsNumber = int.TryParse(leftIdentifiers[index], out var leftNumber);
            var rightIsNumber = int.TryParse(rightIdentifiers[index], out var rightNumber);
            var comparison = leftIsNumber && rightIsNumber
                ? leftNumber.CompareTo(rightNumber)
                : leftIsNumber != rightIsNumber
                    ? (leftIsNumber ? -1 : 1)
                    : string.Compare(leftIdentifiers[index], rightIdentifiers[index], StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) return comparison;
        }
        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    private static (Version Core, string? Prerelease) SplitVersion(string value)
    {
        var parts = value.Split('-', 2);
        var coreText = parts[0].Split('+', 2)[0];
        return (Version.TryParse(coreText, out var core) ? core : new Version(0, 0),
            parts.Length > 1 ? parts[1].Split('+', 2)[0] : null);
    }

    private sealed record CacheEntry(DateTimeOffset CreatedAt, IReadOnlyList<GameLoaderOption> Options);
}
