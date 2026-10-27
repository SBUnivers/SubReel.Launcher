using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.Abstractions.Settings;

namespace SubReel.Launcher.Infrastructure.Persistence;

public sealed class JsonSettingsService(IAppPaths paths, ILogger<JsonSettingsService> logger) : ISettingsService
{
    private const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Normalize(settings);
            await SaveCoreAsync(settings, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateAsync(Action<LauncherSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await LoadCoreAsync(cancellationToken);
            update(settings);
            Normalize(settings);
            await SaveCoreAsync(settings, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<LauncherSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SettingsFile)) return new LauncherSettings();

        try
        {
            LauncherSettings? settings;
            await using (var stream = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var hasSchemaVersion = document.RootElement.TryGetProperty(nameof(LauncherSettings.SchemaVersion), out _) ||
                        document.RootElement.TryGetProperty("schemaVersion", out _);
                    settings = document.RootElement.Deserialize(LauncherSettingsJsonContext.Default.LauncherSettings);
                    if (settings is not null && !hasSchemaVersion)
                        settings.SchemaVersion = 0;
                }
                else settings = null;
            }

            if (settings is null) return RecoverCorruptSettingsFile();

            var originalSchemaVersion = settings.SchemaVersion;
            Normalize(settings);
            if (settings.SchemaVersion != originalSchemaVersion)
                await SaveCoreAsync(settings, cancellationToken);

            return settings;
        }
        catch (JsonException)
        {
            return RecoverCorruptSettingsFile();
        }
    }

    private async Task SaveCoreAsync(LauncherSettings settings, CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        var temporaryPath = paths.SettingsFile + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, LauncherSettingsJsonContext.Default.LauncherSettings, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, paths.SettingsFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void Normalize(LauncherSettings settings)
    {
        Migrate(settings);
        settings.DefaultMemoryMb = Math.Clamp(settings.DefaultMemoryMb, 512, 65536);
        settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 32);
        settings.TextScale = double.IsFinite(settings.TextScale) ? Math.Clamp(settings.TextScale, 0.85, 1.25) : 1d;
    }

    private static void Migrate(LauncherSettings settings)
    {
        if (settings.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Файл настроек создан более новой версией лаунчера (схема {settings.SchemaVersion}). Обнови лаунчер; файл настроек оставлен без изменений.");

        if (settings.SchemaVersion < 0)
            settings.SchemaVersion = 0;

        while (settings.SchemaVersion < CurrentSchemaVersion)
        {
            switch (settings.SchemaVersion)
            {
                case 0:
                    MigrateV0ToV1(settings);
                    break;
                default:
                    throw new InvalidDataException($"Нет миграции настроек из схемы {settings.SchemaVersion}.");
            }
        }
    }

    private static void MigrateV0ToV1(LauncherSettings settings)
    {
        // Legacy settings used the current property names without a schema field.
        // Initializer defaults fill missing fields; existing values remain unchanged.
        settings.SchemaVersion = 1;
    }

    private LauncherSettings RecoverCorruptSettingsFile()
    {
        var backupPath = $"{paths.SettingsFile}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        File.Move(paths.SettingsFile, backupPath);
        logger.LogWarning("Повреждённый файл настроек перемещён в резервную копию {BackupPath}; используются значения по умолчанию", backupPath);
        return new LauncherSettings();
    }
}
