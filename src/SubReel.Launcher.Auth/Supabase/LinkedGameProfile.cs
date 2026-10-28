namespace SubReel.Launcher.Auth.Supabase;

/// <summary>Non-secret Minecraft profile metadata linked to the current SubReel user.</summary>
public sealed record LinkedGameProfile(string MinecraftUuid, string MinecraftUsername, DateTimeOffset? UpdatedAt = null)
{
    public override string ToString() => $"{MinecraftUsername} ({MinecraftUuid})";
}
