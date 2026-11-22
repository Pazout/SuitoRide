using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SuitoRide;

internal static class AppUpdates
{
    const string GitHub = "https://github.com/Pazout/SuitoRide";
    const string GitHubApi = "https://api.github.com/repos/Pazout/SuitoRide";
    const long MaxDownloadBytes = 500L * 1024 * 1024;
    const long MaxExtractBytes = 2L * 1024 * 1024 * 1024;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    internal sealed record Update(Version Version, Uri Download, long Bytes, string Sha256)
    {
        public Uri? ApiDownload { get; init; }
    }
    internal sealed class GitHubAccessException : Exception { }
    static string TokenPath => Path.Combine(Path.GetDirectoryName(InstallFolder)!, "github-updates.dat");

    public static string? LoadToken()
    {
        if (!File.Exists(TokenPath)) return null;
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void SaveToken(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        byte[] plain = Encoding.UTF8.GetBytes(token);
        try { File.WriteAllBytes(TokenPath, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static string InstallFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuitoRide", "installed");

    public static async Task<Update?> FindAsync(Version currentVersion, string? token = null)
    {
        using var request = GitHubRequest(new Uri(GitHubApi + "/releases/latest"), "application/vnd.github+json", token);
        using var response = await Http.SendAsync(request);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            throw new GitHubAccessException();
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ReadUpdate(release.RootElement, currentVersion);
    }

    internal static Update? ReadUpdate(JsonElement release, Version currentVersion)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version.Build < 0 ||
            tag != $"v{version}" || Normalize(version) <= Normalize(currentVersion))
            return null;

        if (RuntimeInformation.OSArchitecture != Architecture.X64 &&
            RuntimeInformation.OSArchitecture != Architecture.Arm64)
            throw new InvalidOperationException("Pro tento počítač je potřeba ruční aktualizace z GitHubu.");

        string name = $"SuitoRide-{tag}-win-x64.zip";
        string expectedUrl = $"{GitHub}/releases/download/{tag}/{name}";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name || asset.GetProperty("state").GetString() != "uploaded" ||
                asset.GetProperty("browser_download_url").GetString() != expectedUrl)
                continue;

            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaxDownloadBytes ||
                !asset.TryGetProperty("digest", out var digestValue) ||
                digestValue.GetString() is not string digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
                break;

            var update = new Update(version, new Uri(expectedUrl), size, digest[7..]);
            if (asset.TryGetProperty("id", out var assetId) && assetId.TryGetInt64(out long id) && id > 0)
                update = update with { ApiDownload = new Uri($"{GitHubApi}/releases/assets/{id}") };
            return update;
        }
        throw new InvalidOperationException($"Verze {version} je na GitHubu, ale chybí ověřitelný ZIP pro Windows x64.");
    }

    public static async Task<string> InstallAsync(Update update, string? token = null)
    {
        Directory.CreateDirectory(InstallFolder);
        string workspace = Path.Combine(InstallFolder, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            string archivePath = Path.Combine(workspace, "update.zip");
            await DownloadAsync(update, archivePath, token);
            string extracted = Path.Combine(workspace, "files");
            await Task.Run(() => Extract(update, archivePath, extracted));

            string destination = Path.Combine(InstallFolder, $"v{update.Version}");
            if (Directory.Exists(destination))
            {
                // Keep a manually edited or previously started installation untouched.
                destination += "-" + Guid.NewGuid().ToString("N")[..8];
            }
            Directory.Move(extracted, destination);
            return Path.Combine(destination, "SuitoRide.exe");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static HttpRequestMessage GitHubRequest(Uri address, string accept, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("SuitoRide");
        request.Headers.Accept.ParseAdd(accept);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    static async Task DownloadAsync(Update update, string destination, string? accessToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        var address = accessToken == null ? update.Download : update.ApiDownload
            ?? throw new InvalidDataException("GitHub neposkytl identifikátor souboru pro soukromé stažení.");
        using var request = GitHubRequest(address, "application/octet-stream", accessToken);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            length += read;
            if (length > update.Bytes) throw new InvalidDataException("Stažený ZIP je větší, než uvádí GitHub.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (length != update.Bytes || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
            throw new InvalidDataException("Velikost nebo kontrolní součet aktualizace neodpovídá údajům na GitHubu.");
    }

    static void Extract(Update update, string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 10000 || archive.Entries.Sum(entry => entry.Length) > MaxExtractBytes)
            throw new InvalidDataException("ZIP aktualizace je po rozbalení příliš velký.");

        archive.ExtractToDirectory(destination);
        string exe = Path.Combine(destination, "SuitoRide.exe");
        if (!File.Exists(exe) || !File.Exists(Path.Combine(destination, "map", "index.html")) ||
            !Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var actual) ||
            Normalize(actual) != Normalize(update.Version))
            throw new InvalidDataException("ZIP neobsahuje očekávanou verzi Suito Ride.");
    }

    static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
}
