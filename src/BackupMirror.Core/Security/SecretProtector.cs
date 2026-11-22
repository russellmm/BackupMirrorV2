using System.Security.Cryptography;
using System.Text;

namespace BackupMirror.Core.Security;

/// <summary>
/// Protects secrets at rest.
/// Windows: DPAPI LocalMachine scope (so the service can decrypt), stored as plain base64 (v1 format).
/// Linux/macOS: AES-256-GCM with a random key in <c>secret.key</c> (mode 0600) under the data root,
/// stored as <c>aes:</c> + base64(nonce | tag | ciphertext).
/// </summary>
public static class SecretProtector
{
    private const string AesPrefix = "aes:";
    private static string? _keyDirectory;
    private static readonly object Gate = new();

    /// <summary>Where the Linux key file lives (call once at startup with the data root).</summary>
    public static void Initialize(string dataRoot) => _keyDirectory = dataRoot;

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return "";

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        if (OperatingSystem.IsWindows())
        {
            var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(protectedBytes);
        }

        var key = LoadOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[bytes.Length];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, bytes, cipher, tag);

        var blob = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, 12);
        cipher.CopyTo(blob, 28);
        return AesPrefix + Convert.ToBase64String(blob);
    }

    public static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            return "";

        if (protectedValue.StartsWith(AesPrefix, StringComparison.Ordinal))
        {
            var blob = Convert.FromBase64String(protectedValue[AesPrefix.Length..]);
            if (blob.Length < 28) throw new CryptographicException("Protected value is truncated.");
            var key = LoadOrCreateKey();
            var plain = new byte[blob.Length - 28];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This password was saved on Windows; re-enter it on this machine.");

        var protectedBytes = Convert.FromBase64String(protectedValue);
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] LoadOrCreateKey()
    {
        var dir = _keyDirectory ?? throw new InvalidOperationException("SecretProtector.Initialize was not called.");
        var path = Path.Combine(dir, "secret.key");
        lock (Gate)
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == 32) return existing;
                throw new CryptographicException($"Key file {path} is corrupt (expected 32 bytes).");
            }

            Directory.CreateDirectory(dir);
            var key = RandomNumberGenerator.GetBytes(32);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(path, options))
                fs.Write(key);
            return key;
        }
    }
}
