using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Abstractions.Paths;

namespace SubReel.Launcher.Auth;

[SupportedOSPlatform("windows")]
public sealed class DpapiTranslationKeyStore(IAppPaths paths, ILogger<DpapiTranslationKeyStore> logger) : ITranslationKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SubReel.Launcher.DeepL.TranslationKey.v1");
    private string FilePath => Path.Combine(paths.Auth, "deepl-key.dpapi");

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(FilePath, cancellationToken);
        byte[] plain;
        try { plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException ex)
        {
            QuarantineCorruptKey(ex);
            return null;
        }
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SaveAsync(string? key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return;
        }
        Directory.CreateDirectory(paths.Auth);
        var plain = Encoding.UTF8.GetBytes(key.Trim());
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            await AtomicFileWriter.WriteAllBytesAsync(FilePath, protectedBytes, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private void QuarantineCorruptKey(CryptographicException exception)
    {
        try
        {
            var backupPath = $"{FilePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
            File.Move(FilePath, backupPath);
            logger.LogWarning(exception, "Повреждённый DPAPI-ключ переводчика перемещён в резервную копию {BackupPath}", backupPath);
        }
        catch (IOException moveException)
        {
            logger.LogError(moveException, "Не удалось переместить повреждённый DPAPI-ключ переводчика в резервную копию");
        }
    }
}
