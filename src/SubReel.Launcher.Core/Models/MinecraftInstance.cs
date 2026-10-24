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

    public string GameDirectory { get; set; } = "";

    public int MemoryMb { get; set; } = 4096;

    public bool IsFavorite { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
        = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastPlayedAt { get; set; }

    [JsonIgnore]
    public bool IsVanilla => LoaderType == MinecraftLoaderType.Vanilla;
}
