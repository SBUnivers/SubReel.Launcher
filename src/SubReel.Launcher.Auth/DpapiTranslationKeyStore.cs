using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Abstractions.Paths;

namespace SubReel.Launcher.Auth;

[SupportedOSPlatform("windows")]
public sealed class DpapiTranslationKeyStore(IAppPaths paths) : ITranslationKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SubReel.Launcher.DeepL.TranslationKey.v1");
    private string FilePath => Path.Combine(paths.Auth, "deepl-key.dpapi");

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(FilePath, cancellationToken);
        var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public async Task SaveAsync(string? key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return;
        }
        Directory.CreateDirectory(paths.Auth);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(FilePath, protectedBytes, cancellationToken);
    }
}
