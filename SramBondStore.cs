using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SuitoRide;

internal static class SramBondStore
{
    internal static byte[]? Load(string directory, string name)
    {
        string path = BondPath(directory, name);
        if (!File.Exists(path)) return null;
        byte[] key = ProtectedData.Unprotect(File.ReadAllBytes(path), Identity(name), DataProtectionScope.CurrentUser);
        if (key.Length == 16) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException();
    }

    internal static void Save(string directory, string name, byte[] key)
    {
        if (key.Length != 16) throw new ArgumentException(nameof(key));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(key, Identity(name), DataProtectionScope.CurrentUser));
            File.Move(temporary, BondPath(directory, name), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    static byte[] Identity(string name) => Encoding.UTF8.GetBytes(name.Trim().ToUpperInvariant());
    static string BondPath(string directory, string name) =>
        Path.Combine(directory, $"sram-bond-{Convert.ToHexString(SHA256.HashData(Identity(name)))[..24]}.dat");
}
