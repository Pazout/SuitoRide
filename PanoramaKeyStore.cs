using System.Security.Cryptography;
using System.Text;

namespace SuitoRide;

internal static class PanoramaKeyStore
{
    static string KeyPath(string provider)
    {
        if (provider != "mapy" && provider != "google" && provider != "google3d") throw new ArgumentException("Neznámý poskytovatel.");
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuitoRide", provider + ".key");
    }
    public static bool Exists(string provider) => System.IO.File.Exists(KeyPath(provider));
    public static void Save(string provider, string key)
    {
        var path=KeyPath(provider);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        byte[] plain=Encoding.UTF8.GetBytes(key);
        try
        {
            byte[] encrypted=ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            System.IO.File.WriteAllBytes(path, encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static string? Load(string provider)
    {
        var path=KeyPath(provider);
        if (!System.IO.File.Exists(path)) return null;
        var encrypted=System.IO.File.ReadAllBytes(path);
        byte[] plain=ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static void Delete(string provider) => System.IO.File.Delete(KeyPath(provider));

    static string ProviderPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuitoRide", "panorama-provider.txt");
    public static void RememberProvider(string provider)
    {
        _ = KeyPath(provider);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ProviderPath)!);
        System.IO.File.WriteAllText(ProviderPath,provider);
    }
    public static string? LastProvider()
    {
        if (System.IO.File.Exists(ProviderPath))
        {
            var provider=System.IO.File.ReadAllText(ProviderPath).Trim();
            if ((provider=="mapy" || provider=="google") && Exists(provider)) return provider;
        }
        if (Exists("mapy")) return "mapy";
        if (Exists("google")) return "google";
        return null;
    }
}