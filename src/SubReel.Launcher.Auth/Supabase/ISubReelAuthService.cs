namespace SubReel.Launcher.Auth.Supabase;

public interface ISubReelAuthService
{
    SubReelAuthSession? CurrentSession { get; }
    bool IsConfigured { get; }
    bool IsBrowserSignInConfigured { get; }
    Task<SubReelAuthResult> SignInWithBrowserAsync(CancellationToken cancellationToken = default);
    Task<SubReelAuthResult> SignInAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<SubReelAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<SubReelAuthResult> RestoreSessionAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
