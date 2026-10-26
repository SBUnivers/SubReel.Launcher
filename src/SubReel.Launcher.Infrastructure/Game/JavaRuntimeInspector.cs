using System.Diagnostics;
using System.Text.RegularExpressions;
using SubReel.Launcher.Abstractions.Game;

namespace SubReel.Launcher.Infrastructure.Game;

public sealed partial class JavaRuntimeInspector : IJavaRuntimeInspector
{
    [GeneratedRegex("version \\\"(?<version>[^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    public async Task<JavaRuntimeInfo> InspectAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Укажи путь к java.exe или javaw.exe.", nameof(executablePath));

        var fullPath = Path.GetFullPath(executablePath.Trim().Trim('"'));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Файл Java не найден.", fullPath);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fullPath,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить выбранный Java runtime.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = (await stderrTask) + "\n" + (await stdoutTask);
            var match = VersionRegex().Match(output);
            if (!match.Success)
                throw new InvalidOperationException("Java запустилась, но её версию не удалось определить.");

            var versionText = match.Groups["version"].Value;
            var firstPart = versionText.Split('.', '-', '+', '_')[0];
            var majorText = firstPart == "1"
                ? versionText.Split('.', '-', '+', '_').Skip(1).FirstOrDefault() ?? ""
                : firstPart;
            if (!int.TryParse(majorText, out var majorVersion))
                throw new InvalidOperationException($"Не удалось разобрать версию Java: {versionText}.");

            return new JavaRuntimeInfo(fullPath, majorVersion, versionText);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Проверка Java превысила 8 секунд.");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }
}
