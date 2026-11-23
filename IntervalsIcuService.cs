using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuitoRide;

internal sealed class IntervalsIcuService
{
    const string Api = "https://intervals.icu/api/v1";
    const string VirtualRide = "VirtualRide";
    static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    readonly HttpClient http;
    readonly string keyPath;

    public IntervalsIcuService(HttpClient? http = null, string? directory = null)
    {
        this.http = http ?? SharedHttp;
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuitoRide");
        keyPath = Path.Combine(directory, "intervals-icu.key");
    }

    public bool IsConnected => File.Exists(keyPath);
    public void Forget() => File.Delete(keyPath);

    public async Task ConnectAsync(string key)
    {
        key = key.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(UiText.Get("Zadej osobní API klíč Intervals.icu."));

        using var request = Request(HttpMethod.Get, "/athlete/0", key);
        using var response = await http.SendAsync(request);
        EnsureAccepted(response);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (string.IsNullOrWhiteSpace(ReadString(result.RootElement, "id")))
            throw new InvalidOperationException(UiText.Get("Intervals.icu nepotvrdil připojení účtu."));

        SaveKey(key);
    }

    public enum UploadStatus { Uploaded, Duplicate, TypeNotConfirmed }
    public sealed record UploadResult(UploadStatus Status, string? ActivityId = null);

    public async Task<UploadResult> UploadAsync(RideRecord ride, string name, string? description)
    {
        string key = LoadKey();
        string externalId = $"suitoride-{ride.Id:N}";
        string path = $"/athlete/0/activities?external_id={externalId}&name={Uri.EscapeDataString(name)}";
        if (!string.IsNullOrWhiteSpace(description)) path += "&description=" + Uri.EscapeDataString(description);

        using var request = Request(HttpMethod.Post, path, key);
        var form = new MultipartFormDataContent();
        request.Content = form;
        var file = new ByteArrayContent(TcxExporter.Create(ride));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        form.Add(file, "file", externalId + ".tcx");
        using var response = await http.SendAsync(request);
        EnsureAccepted(response);
        if (response.StatusCode == HttpStatusCode.OK) return new(UploadStatus.Duplicate);
        if (response.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException(UiText.Get("Intervals.icu nepotvrdil uložení jízdy."));

        string? id = null;
        try
        {
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            id = ActivityId(result.RootElement);
            if (id == null) return new(UploadStatus.TypeNotConfirmed);

            using var update = Request(HttpMethod.Put, "/activity/" + id, key);
            update.Content = JsonContent.Create(new { type = VirtualRide });
            using var updated = await http.SendAsync(update);
            if (updated.IsSuccessStatusCode)
            {
                using var activity = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
                if (ReadString(activity.RootElement, "type") == VirtualRide)
                    return new(UploadStatus.Uploaded, id);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            // The TCX was accepted. A second upload would not fix an unconfirmed metadata update.
        }
        return new(UploadStatus.TypeNotConfirmed, id);
    }

    static string? ActivityId(JsonElement result)
    {
        // The current API wraps uploaded activities in an object. Older responses used a direct array.
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("activities", out var activities) &&
            activities.ValueKind == JsonValueKind.Array && activities.GetArrayLength() > 0)
            result = activities;
        if (result.ValueKind == JsonValueKind.Array)
        {
            if (result.GetArrayLength() != 1) return null;
            result = result[0];
        }
        string? id = ReadString(result, "id");
        return id is { Length: > 1 } && id[0] == 'i' && id.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 ? id : null;
    }

    static string? ReadString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var parsed) &&
        parsed.ValueKind == JsonValueKind.String ? parsed.GetString() : null;

    static HttpRequestMessage Request(HttpMethod method, string path, string key)
    {
        var request = new HttpRequestMessage(method, Api + path);
        byte[] plain = Encoding.UTF8.GetBytes("API_KEY:" + key);
        try { request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    static void EnsureAccepted(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException(UiText.Get("Intervals.icu odmítl přístup. Zkontroluj osobní API klíč."));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(UiText.Format($"Intervals.icu požadavek nepřijal (HTTP {(int)response.StatusCode})."));
    }

    string LoadKey()
    {
        if (!IsConnected) throw new InvalidOperationException(UiText.Get("Nejprve nastav přístup k Intervals.icu."));
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    void SaveKey(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        string temp = keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] plain = Encoding.UTF8.GetBytes(key);
        try
        {
            File.WriteAllBytes(temp, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            File.Move(temp, keyPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
