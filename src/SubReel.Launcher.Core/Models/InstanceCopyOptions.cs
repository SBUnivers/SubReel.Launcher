namespace SubReel.Launcher.Core.Models;

/// <summary>Optional user data to copy when duplicating a Minecraft instance.</summary>
public sealed record InstanceCopyOptions(
    bool Saves = false,
    bool GameOptions = false,
    bool ResourcePacks = false,
    bool ShaderPacks = false,
    bool Screenshots = false,
    bool Servers = false,
    bool Mods = false);
