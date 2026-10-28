using SubReel.Launcher.Rust.Abstractions.Models;

namespace SubReel.Launcher.Rust.Abstractions.Services;

public interface IRustInstallationRepository
{
    Task<IReadOnlyList<RustInstalledClient>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<RustInstalledClient?> GetAsync(string clientId, string releaseTag, CancellationToken cancellationToken = default);
    Task SaveAsync(RustInstalledClient installation, CancellationToken cancellationToken = default);
    Task DeleteAsync(string clientId, string releaseTag, CancellationToken cancellationToken = default);
}
