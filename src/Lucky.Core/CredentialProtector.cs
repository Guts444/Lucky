using System.Security.Cryptography;
using System.Text;

namespace Lucky.Core;

public static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Lucky.LocalHarness.v1");

    public static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Lucky protects credentials with Windows DPAPI.");
        }

        var bytes = Encoding.UTF8.GetBytes(secret);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return null;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Lucky protects credentials with Windows DPAPI.");
            }

            var encrypted = Convert.FromBase64String(protectedSecret);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

}
