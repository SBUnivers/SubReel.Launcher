using SubReel.Launcher.Abstractions.Modpacks;

namespace SubReel.Launcher.Core.Models;

public sealed record InstalledInstanceMod(string FileName, long SizeBytes, bool IsEnabled,
    string? UpdateProjectId = null, string? UpdateVersionId = null, string? UpdateVersionNumber = null,
    string? CurrentProjectId = null, string? CurrentFileId = null, ModpackProvider Provider = ModpackProvider.Modrinth)
{
    public bool HasUpdate => !string.IsNullOrWhiteSpace(UpdateVersionId);
}

public interface IInstanceModService
{
    Task<IReadOnlyList<InstanceModSearchResult>> SearchAsync(MinecraftInstance instance, string query,
        CancellationToken cancellationToken = default, ModpackProvider provider = ModpackProvider.Modrinth);
    Task<IReadOnlyList<InstalledInstanceMod>> GetInstalledAsync(MinecraftInstance instance,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstalledInstanceMod>> CheckUpdatesAsync(MinecraftInstance instance,
        CancellationToken cancellationToken = default);
    Task InstallAsync(MinecraftInstance instance, InstanceModSearchResult mod,
        CancellationToken cancellationToken = default);
    Task UpdateAsync(MinecraftInstance instance, InstalledInstanceMod mod,
        CancellationToken cancellationToken = default);
    Task RemoveAsync(MinecraftInstance instance, string fileName, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(MinecraftInstance instance, string fileName, bool enabled,
        CancellationToken cancellationToken = default);
}
