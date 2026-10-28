using SubReel.Launcher.Rust.Abstractions.Models;

namespace SubReel.Launcher.Rust.Abstractions.Services;

public interface IRustClientInstaller
{
    Task<RustInstalledClient> InstallAsync(
        RustClientRelease release,
        IProgress<RustInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(
        string clientId,
        string releaseTag,
        CancellationToken cancellationToken = default);
}
