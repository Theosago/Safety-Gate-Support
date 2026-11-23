using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SafetyGateSupport.Services;

internal static class MaintainXCredentials
{
    private static string KeyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SafetyGateSupport", "MaintainX", "api-key.dpapi");

    internal static string? Load()
    {
        try
        {
            var key = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(key);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void Save(string key) => OperatorPublishingState.WriteFile(KeyPath,
        ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
}
