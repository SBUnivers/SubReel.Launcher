using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Auth.Supabase;

namespace SubReel.Launcher.App.Services;

public sealed class LauncherSessionProvider(ISubReelAuthService auth) : ILauncherSessionProvider
{
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        var current = auth.CurrentSession;
        if (!forceRefresh && current is not null && current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return current.AccessToken;

        var restored = await auth.RestoreSessionAsync(cancellationToken);
        return restored.Success ? auth.CurrentSession?.AccessToken : null;
    }
}
