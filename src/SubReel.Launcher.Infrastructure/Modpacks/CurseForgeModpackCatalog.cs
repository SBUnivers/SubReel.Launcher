using SubReel.Launcher.Abstractions.Modpacks;

namespace SubReel.Launcher.Infrastructure.Modpacks;

public sealed class CurseForgeModpackCatalog(ICurseForgeApiClient api) : IModpackCatalog
{
    public string Id => "curseforge";
    public string DisplayName => "CurseForge";
    public ModpackProvider Provider => ModpackProvider.CurseForge;

    public async Task<ModpackSearchPage> SearchAsync(ModpackQuery query, CancellationToken cancellationToken = default)
    {
        var result = await api.SearchAsync(query, cancellationToken);
        return new ModpackSearchPage(result.Items, result.TotalHits, query.Offset, query.Limit);
    }

    public async Task<IReadOnlyList<ModpackVersionSummary>> GetVersionsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(projectId, out var id) || id <= 0) throw new ArgumentException("Некорректный CurseForge project ID.", nameof(projectId));
        var files = await api.GetFilesAsync(id, cancellationToken: cancellationToken);
        return files.Where(file => file.IsAvailable && file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Select(file =>
            {
                var loader = file.GameVersions.FirstOrDefault(IsLoader);
                var gameVersions = file.GameVersions.Where(value => !IsLoader(value)).ToArray();
                var sha1 = file.Hashes.FirstOrDefault(hash => hash.Algorithm == 1)?.Value;
                return new ModpackVersionSummary(file.Id.ToString(), file.DisplayName, file.Id.ToString(), gameVersions,
                    loader is null ? [] : [loader], file.FileName, $"curseforge://{id}/{file.Id}", sha1)
                {
                    PublishedAt = file.PublishedAt
                };
            }).ToArray();
    }

    private static bool IsLoader(string value) => value.Equals("Forge", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Fabric", StringComparison.OrdinalIgnoreCase) || value.Equals("Quilt", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("NeoForge", StringComparison.OrdinalIgnoreCase);
}
