using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SubReel.Launcher.Abstractions.Paths;

namespace SubReel.Launcher.App.Services;

public interface IDiagnosticBundleService
{
    Task<string> CreateAsync(CancellationToken cancellationToken = default);
    void OpenLogsFolder();
}

/// <summary>Creates a support archive from launcher logs only; settings, accounts, and game files are excluded.</summary>
public sealed partial class DiagnosticBundleService(IAppPaths paths) : IDiagnosticBundleService
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private const int MaxLogFiles = 4;

    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var fileName = $"subreel-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip";
        var archivePath = Path.Combine(paths.Logs, fileName);
        var logs = Directory.EnumerateFiles(paths.Logs, "launcher-*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => file.Length is > 0 and <= MaxLogBytes)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaxLogFiles)
            .ToArray();

        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            var metadata = $"SubReel Launcher diagnostics\nVersion: {Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown"}\n" +
                $"Windows: {Environment.OSVersion.Version}\nRuntime: {Environment.Version}\nArchitecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n" +
                $"Created: {DateTimeOffset.UtcNow:O}\nIncluded launcher logs: {logs.Length}\n" +
                "This archive excludes launcher settings, account/session files, Minecraft files, and user profile paths.\n";
            await WriteEntryAsync(archive, "diagnostics.txt", metadata, cancellationToken);

            foreach (var log in logs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var input = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
                using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var content = await reader.ReadToEndAsync(cancellationToken);
                await WriteEntryAsync(archive, $"logs/{Path.GetFileName(log.Name)}", Redact(content), cancellationToken);
            }
        }

        return archivePath;
    }

    public void OpenLogsFolder()
    {
        paths.EnsureCreated();
        Process.Start(new ProcessStartInfo { FileName = paths.Logs, UseShellExecute = true });
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static string Redact(string value)
    {
        value = BearerTokenRegex().Replace(value, "$1[REDACTED]");
        value = JwtRegex().Replace(value, "[REDACTED_JWT]");
        value = SecretFieldRegex().Replace(value, "$1[REDACTED]$3");
        value = QuerySecretRegex().Replace(value, "$1[REDACTED]");
        value = EmailRegex().Replace(value, "[REDACTED_EMAIL]");
        value = UserProfilePathRegex().Replace(value, "$1:\\Users\\[USER]");
        return value;
    }

    [GeneratedRegex("(?i)(\\bBearer\\s+)[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex("(?i)([\"']?(?:access[_-]?token|refresh[_-]?token|id[_-]?token|password|client[_-]?secret|service[_-]?role[_-]?key|api[_-]?key|authorization|verifier|code|username|user_id|uuid)\\b[\"']?\\s*[:=]\\s*[\"']?)([^\"'&\\s,}\\]]+)([\"']?)")]
    private static partial Regex SecretFieldRegex();

    [GeneratedRegex("(?i)([?&](?:access_token|refresh_token|token|code|state|verifier)=)[^&#\\s]+")]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex("\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex("(?i)\\b([A-Z]):\\\\Users\\\\[^\\\\\\s\"']+")]
    private static partial Regex UserProfilePathRegex();
}
