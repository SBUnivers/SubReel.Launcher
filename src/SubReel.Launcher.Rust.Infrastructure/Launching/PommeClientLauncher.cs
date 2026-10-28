using System.Diagnostics;
using System.ComponentModel;
using SubReel.Launcher.Rust.Abstractions.Models;
using SubReel.Launcher.Rust.Abstractions.Paths;
using SubReel.Launcher.Rust.Abstractions.Services;

namespace SubReel.Launcher.Rust.Infrastructure.Launching;

public sealed class PommeClientLauncher(
    IRustPaths paths,
    IRustClientInstaller installer,
    IRustProcessStarter processStarter) : IRustClientLauncher
{
    public bool IsClientRunning(RustInstanceRecord instance)
    {
        if (!instance.ClientId.Equals("pomme", StringComparison.OrdinalIgnoreCase)) return false;
        var executable = Path.GetFullPath(Path.Combine(
            paths.GetClientReleaseDirectory(instance.ClientId, instance.ReleaseTag), "pomme-client.exe"));
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    var processPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(processPath)) return true;
                    if (string.Equals(Path.GetFullPath(processPath), executable,
                            StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException)
                {
                    // If Windows cannot inspect a same-named process, refuse destructive removal conservatively.
                    return true;
                }
            }
        }
        return false;
    }

    public async Task<RustLaunchResult> LaunchAsync(
        RustLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Instance);
        cancellationToken.ThrowIfCancellationRequested();
        var instance = request.Instance;
        if (instance.Id == Guid.Empty || !instance.ClientId.Equals("pomme", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Указан некорректный Rust-инстанс.", nameof(request));
        if (string.IsNullOrWhiteSpace(instance.ReleaseTag))
            throw new ArgumentException("У инстанса не указана версия клиента.", nameof(request));
        if (request.Username is { Length: > 16 } || request.Username?.Any(char.IsControl) == true)
            throw new ArgumentException("Некорректный игровой ник.", nameof(request));

        if (!await installer.VerifyAsync(instance.ClientId, instance.ReleaseTag, cancellationToken))
            throw new InvalidOperationException("Клиент Pomme не установлен или его файлы изменены. Переустановите релиз.");
        cancellationToken.ThrowIfCancellationRequested();

        var installDirectory = paths.GetClientReleaseDirectory(instance.ClientId, instance.ReleaseTag);
        var executable = Path.Combine(installDirectory, "pomme-client.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Не найден исполняемый файл Pomme.", executable);

        var instanceDirectory = paths.GetInstanceDirectory(instance.Id);
        var assetsDirectory = Path.Combine(instanceDirectory, "assets");
        var versionsDirectory = Path.Combine(instanceDirectory, "versions");
        var gameDirectory = Path.Combine(instanceDirectory, "game");
        Directory.CreateDirectory(assetsDirectory);
        Directory.CreateDirectory(versionsDirectory);
        Directory.CreateDirectory(gameDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = installDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add("--assets-dir");
        startInfo.ArgumentList.Add(assetsDirectory);
        startInfo.ArgumentList.Add("--versions-dir");
        startInfo.ArgumentList.Add(versionsDirectory);
        startInfo.ArgumentList.Add("--game-dir");
        startInfo.ArgumentList.Add(gameDirectory);
        // Presence of this argument disables Pomme's built-in updater. The SubReel release manager owns updates.
        startInfo.ArgumentList.Add("--launch-token");
        startInfo.ArgumentList.Add(Path.Combine(instanceDirectory, "launcher-managed-update"));
        if (!string.IsNullOrWhiteSpace(instance.GameVersion))
        {
            startInfo.ArgumentList.Add("--version");
            startInfo.ArgumentList.Add(instance.GameVersion);
        }
        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            startInfo.ArgumentList.Add("--username");
            startInfo.ArgumentList.Add(request.Username);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var processId = processStarter.Start(startInfo);
        return new RustLaunchResult(instance.Id, processId, startedAt);
    }
}
