using SubReel.Launcher.Core.Models;

namespace SubReel.Launcher.Core.Instances;

public interface IInstanceRepository
{
    Task<IReadOnlyList<MinecraftInstance>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<MinecraftInstance?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}