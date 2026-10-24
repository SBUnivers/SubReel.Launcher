namespace SubReel.Launcher.Auth.Supabase;

public sealed record SubReelAuthSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string UserId,
    string? Email);

public sealed record SubReelAuthResult(bool Success, bool RequiresEmailConfirmation, string Message, SubReelAuthSession? Session = null);
