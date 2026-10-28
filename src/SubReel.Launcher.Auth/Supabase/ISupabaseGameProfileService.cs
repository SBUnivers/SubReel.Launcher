namespace SubReel.Launcher.Auth.Supabase;

public interface ISupabaseGameProfileService
{
    Task<IReadOnlyList<LinkedGameProfile>> GetLinkedProfilesAsync(CancellationToken cancellationToken = default);
    Task<LinkedGameProfile> LinkCurrentMicrosoftProfileAsync(CancellationToken cancellationToken = default);
    Task UnlinkProfileAsync(string minecraftUuid, CancellationToken cancellationToken = default);
}
