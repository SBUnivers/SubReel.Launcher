using System.Diagnostics;
using System.Text;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Abstractions.Paths;

namespace SubReel.Launcher.Infrastructure.Game;

/// <summary>CmlLib adapter with a shared immutable game cache and per-instance game directories.</summary>
public sealed class CmlLibGameService(IAppPaths paths, IHttpClientFactory httpClientFactory, ILogger<CmlLibGameService> logger) : IGameService
{
    public async Task<Process> LaunchAsync(
        GameLaunchRequest request,
        IProgress<GameInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.MinecraftVersion)) throw new ArgumentException("Версия Minecraft не указана.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PlayerName)) throw new ArgumentException("Ник игрока не указан.", nameof(request));
        if (request.MemoryMb is < 512 or > 65536) throw new ArgumentOutOfRangeException(nameof(request.MemoryMb));
        cancellationToken.ThrowIfCancellationRequested();

        paths.EnsureCreated();
        Directory.CreateDirectory(request.GameDirectory);
        var minecraftPath = new MinecraftPath(Path.GetFullPath(request.GameDirectory))
        {
            Library = Path.GetFullPath(paths.SharedLibraries),
            Versions = Path.GetFullPath(paths.SharedVersions),
            Assets = Path.GetFullPath(paths.SharedAssets),
            Runtime = Path.GetFullPath(paths.SharedRuntime),
            Resource = Path.GetFullPath(paths.SharedResources)
        };
        minecraftPath.CreateDirs();

        var httpClient = httpClientFactory.CreateClient("Minecraft");
        var parameters = MinecraftLauncherParameters.CreateDefault(minecraftPath, httpClient);
        var launcher = new MinecraftLauncher(parameters);
        var lastProgressAt = 0L;
        var progressLock = new object();
        void Report(GameInstallProgress update, bool force = false)
        {
            if (progress is null) return;
            lock (progressLock)
            {
                var now = Environment.TickCount64;
                if (!force && lastProgressAt != 0 && now - lastProgressAt < 100) return;
                lastProgressAt = now;
            }
            progress.Report(update);
        }

        Report(new GameInstallProgress($"Подготовка Minecraft {request.MinecraftVersion}…", 0, 1), force: true);
        var gameVersion = await InstallLoaderAsync(request, launcher, httpClient, message =>
            Report(new GameInstallProgress(message, 0, 1)), cancellationToken);

        var fileProgress = new CallbackProgress<InstallerProgressChangedEventArgs>(update =>
            Report(new GameInstallProgress(update.Name ?? "Загрузка файлов", update.ProgressedTasks, update.TotalTasks), update.TotalTasks > 0 && update.ProgressedTasks >= update.TotalTasks));
        var byteProgress = new CallbackProgress<ByteProgress>(update =>
            Report(new GameInstallProgress("Загрузка файлов Minecraft", 0, 0, update.ProgressedBytes, update.TotalBytes), update.TotalBytes > 0 && update.ProgressedBytes >= update.TotalBytes));

        await launcher.InstallAsync(gameVersion, fileProgress, byteProgress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var options = new MLaunchOption
        {
            Session = request.Session is { } account
                ? new MSession(account.Username, account.Uuid, account.AccessToken) { UserType = "msa", Xuid = account.Xuid }
                : MSession.CreateOfflineSession(request.PlayerName),
            MaximumRamMb = request.MemoryMb,
            MinimumRamMb = Math.Min(1024, request.MemoryMb)
        };
        if (!string.IsNullOrWhiteSpace(request.JavaPath)) options.JavaPath = request.JavaPath;
        if (!string.IsNullOrWhiteSpace(request.JvmArguments))
            options.ExtraJvmArguments = [MArgument.FromCommandLine(request.JvmArguments)];

        var process = await launcher.BuildProcessAsync(gameVersion, options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Report(new GameInstallProgress("Запуск Java и ожидание окна Minecraft…", 0, 0), force: true);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) logger.LogInformation("[Minecraft:{InstanceId}] {Line}", request.InstanceId, args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) logger.LogWarning("[Minecraft:{InstanceId}] {Line}", request.InstanceId, args.Data); };
        if (!process.Start()) throw new InvalidOperationException("Java-процесс Minecraft не запустился.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = ObserveExitAsync(process, request.GameDirectory, request.InstanceId);

        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var deadline = Stopwatch.StartNew();
            while (!process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(45))
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) break;
                await Task.Delay(250, waitCts.Token);
            }
            if (process.HasExited)
                throw new InvalidOperationException($"Minecraft завершился до появления окна (код {process.ExitCode}). Проверьте crash-reports в папке игры.");
            if (process.MainWindowHandle == IntPtr.Zero)
                logger.LogWarning("Minecraft process {InstanceId} is running but did not create a window before timeout", request.InstanceId);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }

        Report(new GameInstallProgress("Minecraft запущен", 1, 1), force: true);
        return process;
    }

    private static async Task<string> InstallLoaderAsync(
        GameLaunchRequest request,
        MinecraftLauncher launcher,
        HttpClient httpClient,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        var loader = request.LoaderType.Trim();
        var loaderVersion = request.LoaderVersion?.Trim();
        if (string.IsNullOrEmpty(loader) || loader.Equals("Vanilla", StringComparison.OrdinalIgnoreCase)) return request.MinecraftVersion;
        cancellationToken.ThrowIfCancellationRequested();
        report($"Установка {loader}…");
        string versionName;
        switch (loader.ToUpperInvariant())
        {
            case "FABRIC":
                var fabric = new FabricInstaller(httpClient);
                versionName = string.IsNullOrWhiteSpace(loaderVersion)
                    ? await fabric.Install(request.MinecraftVersion, launcher.MinecraftPath)
                    : await fabric.Install(request.MinecraftVersion, loaderVersion, launcher.MinecraftPath);
                break;
            case "FORGE":
                var forge = new ForgeInstaller(launcher);
                var forgeOptions = new ForgeInstallOptions { CancellationToken = cancellationToken, SkipIfAlreadyInstalled = true, InstallerOutput = new Progress<string>(report) };
                versionName = string.IsNullOrWhiteSpace(loaderVersion) ? await forge.Install(request.MinecraftVersion, forgeOptions) : await forge.Install(request.MinecraftVersion, loaderVersion, forgeOptions);
                break;
            case "NEOFORGE":
                var neoForge = new NeoForgeInstaller(launcher);
                var neoForgeOptions = new ForgeInstallOptions { CancellationToken = cancellationToken, SkipIfAlreadyInstalled = true, InstallerOutput = new Progress<string>(report) };
                versionName = string.IsNullOrWhiteSpace(loaderVersion) ? await neoForge.Install(request.MinecraftVersion, neoForgeOptions) : await neoForge.Install(request.MinecraftVersion, loaderVersion, neoForgeOptions);
                break;
            default: throw new NotSupportedException($"Установщик для {loader} пока не подключён.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        report($"{loader} установлен");
        return versionName;
    }

    private async Task ObserveExitAsync(Process process, string gameDirectory, Guid? instanceId)
    {
        try
        {
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                logger.LogInformation("Minecraft process {InstanceId} exited normally", instanceId);
                return;
            }
            var crashDirectory = Path.Combine(gameDirectory, "crash-reports");
            var latestCrash = Directory.Exists(crashDirectory)
                ? Directory.GetFiles(crashDirectory, "*.txt").Select(file => new FileInfo(file)).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()
                : null;
            if (latestCrash is null) logger.LogError("Minecraft process {InstanceId} exited with code {ExitCode}; no crash report was found", instanceId, process.ExitCode);
            else logger.LogError("Minecraft process {InstanceId} exited with code {ExitCode}; crash report: {CrashReport}", instanceId, process.ExitCode, latestCrash.FullName);
        }
        catch (Exception ex) { logger.LogError(ex, "Could not observe Minecraft process {InstanceId}", instanceId); }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
