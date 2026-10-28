using SubReel.Launcher.Rust.Abstractions.Models;

namespace SubReel.Launcher.Rust.Abstractions.Services;

public interface IRustInstanceRepository
{
    Task<IReadOnlyList<RustInstanceRecord>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RustInstanceRecord instance, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
