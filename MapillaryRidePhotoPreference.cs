using System.IO;

namespace SuitoRide;

internal static class MapillaryRidePhotoPreference
{
    static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuitoRide", "mapillary-ride-photos.txt");

    public static bool Load()
    {
        try { return bool.TryParse(File.ReadAllText(PreferencePath), out bool enabled) && enabled; }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static void Save(bool enabled)
    {
        string path = PreferencePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, enabled.ToString());
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
