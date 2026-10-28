using System.IO.Compression;
using System.Security.Cryptography;
using SubReel.Launcher.Rust.Abstractions.Models;
using SubReel.Launcher.Rust.Abstractions.Paths;
using SubReel.Launcher.Rust.Abstractions.Services;

namespace SubReel.Launcher.Rust.Infrastructure.Installers;

/// <summary>Downloads and installs the official flat Windows x64 Pomme release bundle.</summary>
public sealed class PommeClientInstaller(
    HttpClient httpClient,
    IRustPaths paths,
    IRustInstallationRepository installations) : IRustClientInstaller
{
    private const string PommeClientId = "pomme";
    private const string WindowsBundleName = "pomme-client-windows-x64.zip";
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;

    public async Task<RustInstalledClient> InstallAsync(
        RustClientRelease release,
        IProgress<RustInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!release.ClientId.Equals(PommeClientId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Этот установщик поддерживает только Pomme.", nameof(release));
        var asset = release.WindowsX64Assets.FirstOrDefault(item => item.Name.Equals(WindowsBundleName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"В релизе {release.Tag} отсутствует архив Windows x64 Pomme.");
        ValidateAsset(release, asset);
        cancellationToken.ThrowIfCancellationRequested();

        paths.EnsureCreated();
        var installDirectory = paths.GetClientReleaseDirectory(release.ClientId, release.Tag);
        var parent = Path.GetDirectoryName(installDirectory)!;
        Directory.CreateDirectory(parent);
        var suffix = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(parent, $".download-{suffix}.zip");
        var stagingDirectory = Path.Combine(parent, $".stage-{suffix}");
        var backupDirectory = Path.Combine(parent, $".backup-{suffix}");

        try
        {
            var archiveDigest = await DownloadAndVerifyAsync(asset, archivePath, progress, cancellationToken);
            progress?.Report(new RustInstallProgress(RustInstallStage.Extracting, FileName: asset.Name));
            var fileHashes = await ExtractAndHashAsync(archivePath, stagingDirectory, progress, cancellationToken);
            if (!fileHashes.Keys.Any(name => name.Equals("pomme-client.exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("В архиве Pomme отсутствует pomme-client.exe.");

            var movedOldInstallation = false;
            var movedNewInstallation = false;
            try
            {
                if (Directory.Exists(installDirectory))
                {
                    Directory.Move(installDirectory, backupDirectory);
                    movedOldInstallation = true;
                }

                Directory.Move(stagingDirectory, installDirectory);
                movedNewInstallation = true;
                var installed = new RustInstalledClient(
                    release.ClientId,
                    release.Tag,
                    DateTimeOffset.UtcNow,
                    archiveDigest,
                    fileHashes);
                progress?.Report(new RustInstallProgress(RustInstallStage.Registering));
                await installations.SaveAsync(installed, cancellationToken);
                if (movedOldInstallation) TryDeleteDirectory(backupDirectory);
                return installed;
            }
            catch
            {
                if (movedNewInstallation && Directory.Exists(installDirectory))
                    Directory.Delete(installDirectory, recursive: true);
                if (movedOldInstallation && Directory.Exists(backupDirectory) && !Directory.Exists(installDirectory))
                    Directory.Move(backupDirectory, installDirectory);
                throw;
            }
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public async Task<bool> VerifyAsync(
        string clientId,
        string releaseTag,
        CancellationToken cancellationToken = default)
    {
        if (!clientId.Equals(PommeClientId, StringComparison.OrdinalIgnoreCase)) return false;
        var installation = await installations.GetAsync(clientId, releaseTag, cancellationToken);
        if (installation is null || installation.FileSha256.Count == 0) return false;
        var directory = paths.GetClientReleaseDirectory(clientId, releaseTag);
        if (!Directory.Exists(directory)) return false;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;

        var expectedNames = new HashSet<string>(installation.FileSha256.Keys, StringComparer.OrdinalIgnoreCase);
        if (expectedNames.Count != installation.FileSha256.Count) return false;
        var actualNames = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualNames.SetEquals(expectedNames)) return false;

        foreach (var expected in installation.FileSha256)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(expected.Key) != expected.Key || expected.Key is "." or ".." ||
                expected.Value is not { Length: 64 } || !expected.Value.All(Uri.IsHexDigit)) return false;
            var file = Path.Combine(directory, expected.Key);
            if (!File.Exists(file)) return false;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!actual.Equals(expected.Value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return installation.FileSha256.Keys.Any(name => name.Equals("pomme-client.exe", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> DownloadAndVerifyAsync(
        RustClientAsset asset,
        string archivePath,
        IProgress<RustInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(asset.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var contentLength = response.Content.Headers.ContentLength ?? asset.SizeBytes;
        if (contentLength is > MaxArchiveBytes) throw new InvalidDataException("Архив Pomme превышает допустимый размер загрузки.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            downloaded += read;
            if (downloaded > MaxArchiveBytes) throw new InvalidDataException("Архив Pomme превышает допустимый размер загрузки.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new RustInstallProgress(RustInstallStage.Downloading, downloaded, contentLength, asset.Name));
        }
        await output.FlushAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new RustInstallProgress(RustInstallStage.Verifying, downloaded, contentLength, asset.Name));

        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 архива Pomme не совпадает с опубликованной контрольной суммой.");
        return actual;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ExtractAndHashAsync(
        string archivePath,
        string stagingDirectory,
        IProgress<RustInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > 2048) throw new InvalidDataException("Архив Pomme содержит недопустимое количество файлов.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalExpanded = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!string.Equals(entry.FullName, entry.Name, StringComparison.Ordinal) ||
                entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || entry.Name is "." or "..")
                throw new InvalidDataException("Архив Pomme содержит путь файла вне корня пакета.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Символические ссылки в архиве Pomme запрещены.");
            if (!result.TryAdd(entry.Name, string.Empty)) throw new InvalidDataException("Архив Pomme содержит повторяющиеся имена файлов.");
            if (entry.Length < 0 || totalExpanded + entry.Length > MaxExpandedBytes)
                throw new InvalidDataException("Распакованный архив Pomme превышает допустимый размер.");

            var filePath = Path.Combine(stagingDirectory, entry.Name);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = entry.Open();
            await using var output = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                totalExpanded += read;
                if (totalExpanded > MaxExpandedBytes)
                    throw new InvalidDataException("Фактический размер распаковки Pomme превышает допустимый лимит.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
            if (new FileInfo(filePath).Length != entry.Length)
                throw new InvalidDataException("Размер распакованного файла не совпадает с размером в архиве.");
            result[entry.Name] = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            progress?.Report(new RustInstallProgress(RustInstallStage.Extracting, FileName: entry.Name));
        }

        return result;
    }

    private static void ValidateAsset(RustClientRelease release, RustClientAsset asset)
    {
        if (asset.Sha256 is not { Length: 64 } || !asset.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Для архива Pomme нет корректной опубликованной SHA-256.");
        var expectedReleasePath = "/PommeMC/Client/releases/download/";
        if (asset.DownloadUri.Scheme != Uri.UriSchemeHttps ||
            !asset.DownloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !asset.DownloadUri.AbsolutePath.StartsWith(expectedReleasePath, StringComparison.OrdinalIgnoreCase) ||
            !asset.DownloadUri.AbsolutePath.EndsWith("/" + Uri.EscapeDataString(asset.Name), StringComparison.OrdinalIgnoreCase) ||
            !asset.DownloadUri.AbsolutePath.Contains(Uri.EscapeDataString(release.Tag), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Источник архива не соответствует официальному релизу Pomme на GitHub.");
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
