using SubReel.Launcher.Rust.Abstractions.Models;

namespace SubReel.Launcher.Rust.Abstractions.Services;

public interface IRustClientLauncher
{
    bool IsClientRunning(RustInstanceRecord instance);

    Task<RustLaunchResult> LaunchAsync(
        RustLaunchRequest request,
        CancellationToken cancellationToken = default);
}
