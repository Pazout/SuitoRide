using System.IO;
using System.Text;
using System.Text.Json;

namespace SuitoRide;

internal sealed class DropboxRideArchive
{
    readonly string settingsPath;

    public DropboxRideArchive(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuitoRide");
        settingsPath = Path.Combine(directory, "dropbox-ride-folder.txt");
    }

    public string? LoadFolder()
    {
        if (!File.Exists(settingsPath)) return null;
        string folder = File.ReadAllText(settingsPath).Trim();
        return folder.Length == 0 ? null : folder;
    }

    public void SaveFolder(string? folder)
    {
        if (folder == null)
        {
            File.Delete(settingsPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        WriteAtomic(settingsPath, Encoding.UTF8.GetBytes(Path.GetFullPath(folder)), overwrite: true);
    }

    public bool Export(RideRecord ride)
    {
        string? folder = LoadFolder();
        if (folder == null) return false;
        string filename = FormattableString.Invariant($"SuitoRide-{ride.StartedAt:yyyy-MM-dd-HHmm}-{ride.Id:N}.tcx");
        string target = Path.Combine(folder, filename);
        if (!File.Exists(target))
        {
            try { WriteAtomic(target, TcxExporter.Create(ride), overwrite: false); }
            catch (IOException) when (File.Exists(target)) { }
        }
        return true;
    }

    static void WriteAtomic(string target, byte[] bytes, bool overwrite)
    {
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, target, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static string? FindDropboxFolder()
    {
        foreach (var location in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            string info = Path.Combine(Environment.GetFolderPath(location), "Dropbox", "info.json");
            if (!File.Exists(info)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(info));
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var account in document.RootElement.EnumerateObject())
                {
                    if (account.Value.ValueKind != JsonValueKind.Object ||
                        !account.Value.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) continue;
                    string? folder = path.GetString();
                    if (Path.IsPathFullyQualified(folder ?? "") && Directory.Exists(folder)) return folder;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return null;
    }
}
