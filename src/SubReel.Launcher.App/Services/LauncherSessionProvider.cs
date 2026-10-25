using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Auth.Supabase;

namespace SubReel.Launcher.App.Services;

public sealed class LauncherSessionProvider(ISubReelAuthService auth) : ILauncherSessionProvider
{
    public string? AccessToken => auth.CurrentSession?.AccessToken;
}
