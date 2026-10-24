using System.Text.Json.Serialization;
using SubReel.Launcher.Abstractions.Settings;

namespace SubReel.Launcher.Infrastructure.Persistence;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LauncherSettings))]
internal partial class LauncherSettingsJsonContext : JsonSerializerContext;
