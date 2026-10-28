using SubReel.Launcher.Rust.Abstractions.Models;

namespace SubReel.Launcher.Rust.Abstractions.Services;

public interface IRustClientCatalog
{
    IReadOnlyList<RustClientDescriptor> GetClients();
    Task<IReadOnlyList<RustClientRelease>> GetReleasesAsync(
        string clientId,
        CancellationToken cancellationToken = default);
}
