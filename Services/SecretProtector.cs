using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LectureSmith.Services;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedData);
}

/// <summary>
/// Encrypts and decrypts secrets per-user using Windows DPAPI.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LectureSmith.ApiKey.v1");

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public string Unprotect(string protectedData)
    {
        if (string.IsNullOrWhiteSpace(protectedData))
            return string.Empty;

        try
        {
            var bytes = Convert.FromBase64String(protectedData);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Encoding.UTF8.GetString(bytes);

            var decrypted = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return string.Empty;
        }
    }
}
