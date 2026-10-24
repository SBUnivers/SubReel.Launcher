using SubReel.Launcher.Abstractions.Game;

namespace SubReel.Launcher.Auth.Supabase;

public interface ISupabaseGameProfileService
{
    Task<LinkedGameProfile?> GetLinkedProfileAsync(CancellationToken cancellationToken = default);
    Task<LinkedGameProfile> LinkCurrentMicrosoftProfileAsync(CancellationToken cancellationToken = default);
    Task UnlinkProfileAsync(CancellationToken cancellationToken = default);
}
