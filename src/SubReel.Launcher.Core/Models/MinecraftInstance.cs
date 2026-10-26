using System.Text.Json.Serialization;

namespace SubReel.Launcher.Core.Models;

public sealed class MinecraftInstance
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Новая сборка";

    public string MinecraftVersion { get; set; } = "";

    public MinecraftLoaderType LoaderType { get; set; }
        = MinecraftLoaderType.Vanilla;

    public string? LoaderVersion { get; set; }

    public string? IconPath { get; set; }

    public string? JavaPath { get; set; }

    public string? JvmArguments { get; set; }

    public string Source { get; set; } = "Manual";

    /// <summary>Structured catalog metadata for an installed modpack. Null for manually created instances and older records.</summary>
    public InstalledModpackManifest? ModpackManifest { get; set; }

    public InstanceInstallState InstallState { get; set; } = InstanceInstallState.Ready;

    public string GameDirectory { get; set; } = "";

    public int MemoryMb { get; set; } = 4096;

    public bool IsFavorite { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
        = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastPlayedAt { get; set; }

    [JsonIgnore]
    public bool IsVanilla => LoaderType == MinecraftLoaderType.Vanilla;

    [JsonIgnore]
    public string DisplayName => $"{Name} · {MinecraftVersion} · {LoaderType}";
}

public enum InstanceInstallState
{
    Ready,
    Installing
}

public sealed class InstalledModpackManifest
{
    public string Provider { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ProjectSlug { get; set; } = "";
    public string ProjectTitle { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string VersionName { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public string Loader { get; set; } = "Vanilla";
    public string? LoaderVersion { get; set; }
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}
