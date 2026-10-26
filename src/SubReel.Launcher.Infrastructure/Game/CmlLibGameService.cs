using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Abstractions.Paths;

namespace SubReel.Launcher.Infrastructure.Game;

/// <summary>CmlLib adapter with a shared immutable game cache and per-instance game directories.</summary>
public sealed class CmlLibGameService(
    IAppPaths paths,
    IHttpClientFactory httpClientFactory,
    IJavaRuntimeInspector javaRuntimeInspector,
    IGameVersionCatalog versionCatalog,
    ILogger<CmlLibGameService> logger) : IGameService
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
        if (!string.IsNullOrWhiteSpace(request.JavaPath) && !File.Exists(request.JavaPath))
            throw new FileNotFoundException("Указанный Java runtime не найден. Проверь путь Java в настройках сборки или лаунчера.", request.JavaPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(request.JavaPath))
        {
            var java = await javaRuntimeInspector.InspectAsync(request.JavaPath, cancellationToken);
            int? requiredMajor = null;
            try
            {
                requiredMajor = await versionCatalog.GetRequiredJavaMajorVersionAsync(request.MinecraftVersion, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Could not verify Java requirement for Minecraft {Version}; continuing with Java {JavaMajor}", request.MinecraftVersion, java.MajorVersion);
            }

            if (requiredMajor is not null && java.MajorVersion < requiredMajor)
                throw new InvalidOperationException(
                    $"Для Minecraft {request.MinecraftVersion} требуется Java {requiredMajor} или новее, " +
                    $"а выбран Java {java.MajorVersion}. Укажи совместимый Java runtime в настройках сборки или лаунчера.");
        }

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

        Report(new GameInstallProgress(GameInstallStage.Resolving, $"Подготовка Minecraft {request.MinecraftVersion}…", 0, 1), force: true);
        var gameVersion = await InstallLoaderAsync(request, launcher, httpClient, message =>
            Report(new GameInstallProgress(GameInstallStage.InstallingLoader, message, 0, 1)), cancellationToken);

        var fileProgress = new CallbackProgress<InstallerProgressChangedEventArgs>(update =>
            Report(new GameInstallProgress(GameInstallStage.DownloadingFiles, update.Name ?? "Загрузка файлов", update.ProgressedTasks, update.TotalTasks), update.TotalTasks > 0 && update.ProgressedTasks >= update.TotalTasks));
        var byteProgress = new CallbackProgress<ByteProgress>(update =>
            Report(new GameInstallProgress(GameInstallStage.DownloadingFiles, "Загрузка файлов Minecraft", 0, 0, update.ProgressedBytes, update.TotalBytes), update.TotalBytes > 0 && update.ProgressedBytes >= update.TotalBytes));

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
        var processStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(new GameInstallProgress(GameInstallStage.StartingGame, "Запуск Java и ожидание окна Minecraft…", 0, 0), force: true);
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
            processStarted = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var deadline = Stopwatch.StartNew();
            while (!process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(45))
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) break;
                await Task.Delay(250, waitCts.Token);
            }
            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                var crashReport = FindLatestCrashReport(process, request.GameDirectory);
                var reportMessage = crashReport is null
                    ? " Свежий crash-report не найден."
                    : $" Отчёт: {crashReport.FullName}";
                logger.LogError("Minecraft process {InstanceId} exited before its window appeared with code {ExitCode}; crash report: {CrashReport}",
                    request.InstanceId, exitCode, crashReport?.FullName ?? "none");
                throw new InvalidOperationException(
                    $"Minecraft завершился до появления окна (код {exitCode}).{reportMessage}");
            }
            if (process.MainWindowHandle == IntPtr.Zero)
                logger.LogWarning("Minecraft process {InstanceId} is running but did not create a window before timeout", request.InstanceId);
        }
        catch
        {
            await StopAndDisposeFailedProcessAsync(process, processStarted);
            throw;
        }

        Report(new GameInstallProgress(GameInstallStage.Running, "Minecraft запущен", 1, 1), force: true);
        return process;
    }

    private async Task StopAndDisposeFailedProcessAsync(Process process, bool processStarted)
    {
        try
        {
            if (processStarted && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            logger.LogWarning(ex, "Could not fully stop failed Minecraft process {ProcessId}", SafeGetProcessId(process));
        }
        finally
        {
            process.Dispose();
        }
    }

    private static int? SafeGetProcessId(Process process)
    {
        try { return process.Id; }
        catch (InvalidOperationException) { return null; }
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
            case "QUILT":
                var quilt = new QuiltInstaller(httpClient);
                versionName = string.IsNullOrWhiteSpace(loaderVersion)
                    ? await quilt.Install(request.MinecraftVersion, launcher.MinecraftPath)
                    : await quilt.Install(request.MinecraftVersion, loaderVersion, launcher.MinecraftPath);
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

    public async Task<GameProcessExitInfo> WaitForExitAsync(Process process, string gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(process);
        await process.WaitForExitAsync();
        var exitCode = process.ExitCode;
        if (exitCode == 0)
        {
            logger.LogInformation("Minecraft process {ProcessId} exited normally", process.Id);
            return new GameProcessExitInfo(exitCode);
        }

        var latestCrash = FindLatestCrashReport(process, gameDirectory);
        if (latestCrash is null)
            logger.LogError("Minecraft process {ProcessId} exited with code {ExitCode}; no fresh crash report was found", process.Id, exitCode);
        else
            logger.LogError("Minecraft process {ProcessId} exited with code {ExitCode}; crash report: {CrashReport}", process.Id, exitCode, latestCrash.FullName);
        return new GameProcessExitInfo(exitCode, latestCrash?.FullName);
    }

    private FileInfo? FindLatestCrashReport(Process process, string gameDirectory)
    {
        var crashDirectory = Path.Combine(gameDirectory, "crash-reports");
        DateTime processStartedAtUtc;
        try
        {
            processStartedAtUtc = process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            logger.LogWarning(ex, "Could not determine the start time for Minecraft process {ProcessId}", process.Id);
            processStartedAtUtc = DateTime.UtcNow;
        }

        try
        {
            return Directory.Exists(crashDirectory)
                ? Directory.GetFiles(crashDirectory, "*.txt")
                    .Select(file => new FileInfo(file))
                    // File timestamps can have coarse precision on some file systems.
                    // The small tolerance avoids missing a report from this run while
                    // excluding crash reports left by older sessions.
                    .Where(file => file.LastWriteTimeUtc >= processStartedAtUtc.AddSeconds(-2))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not inspect crash reports for Minecraft process {ProcessId}", process.Id);
            return null;
        }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
