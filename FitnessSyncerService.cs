using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace SuitoRide;

internal sealed class FitnessSyncerService
{
    const string Scope = "notebook_activity_write";
    const string Issuer = "https://www.fitnesssyncer.com";
    const string Api = "https://api.fitnesssyncer.com/api";
    static readonly Uri Redirect = new("https://personal.fitnesssyncer.com/");
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(45) };
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SuitoRide", "fitnesssyncer");
    static string TokenPath => Path.Combine(Folder, "tokens.dat");

    internal sealed record Authorization(string ClientId, string Verifier, string State, Uri Url);
    sealed record Tokens(string ClientId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
    sealed record TokenResponse(
        [property:JsonPropertyName("access_token")] string Access,
        [property:JsonPropertyName("refresh_token")] string Refresh,
        [property:JsonPropertyName("expires_in")] int Lifetime,
        [property:JsonPropertyName("scope")] string? Scope);

    public bool IsConnected => File.Exists(TokenPath);

    public Authorization BeginAuthorization(string clientId)
    {
        clientId = clientId.Trim();
        if (clientId.Length != 32 || !clientId.All(Uri.IsHexDigit))
            throw new ArgumentException(UiText.Get("Zadej 32znakové ID osobní aplikace FitnessSyncer."));

        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var query = HttpUtility.ParseQueryString("");
        query["client_id"] = clientId;
        query["response_type"] = "code";
        query["redirect_uri"] = Redirect.AbsoluteUri;
        query["scope"] = Scope;
        query["state"] = state;
        query["code_challenge"] = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        query["code_challenge_method"] = "S256";
        var url = new UriBuilder(Issuer + "/api/oauth/authorize") { Query = query.ToString() };
        return new(clientId, verifier, state, url.Uri);
    }

    internal static string ReadAuthorizationCode(Authorization authorization, string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var callback) ||
            callback.GetLeftPart(UriPartial.Path) != Redirect.AbsoluteUri ||
            callback.UserInfo.Length != 0 || callback.Fragment.Length != 0)
            throw new ArgumentException(UiText.Get("Vlož celou adresu https://personal.fitnesssyncer.com/ z prohlížeče."));

        var query = HttpUtility.ParseQueryString(callback.Query);
        if (query["state"] != authorization.State ||
            (query["iss"] is string issuer && issuer != Issuer))
            throw new ArgumentException(UiText.Get("Adresa nepatří k tomuto přihlášení. Otevři autorizaci znovu v aplikaci."));
        if (query["error"] != null || query["code"] is not string code || code.Length == 0 || code == "error")
            throw new ArgumentException(UiText.Get("FitnessSyncer nepovolil připojení nebo v adrese chybí autorizační kód."));
        return code;
    }

    public async Task ConnectAsync(Authorization authorization, string address)
    {
        string code = ReadAuthorizationCode(authorization, address);
        var tokens = await RequestTokensAsync(authorization.ClientId, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = authorization.Verifier
        });
        StoreTokens(tokens);
    }

    public async Task UploadAsync(RideRecord ride)
    {
        var tokens = LoadTokens();
        if (tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            tokens = await RefreshAsync(tokens);
        }
        using var response = await UploadTcxAsync(tokens, ride);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            await VerifyUploadAsync(response);
            return;
        }

        tokens = await RefreshAsync(tokens);
        using var retry = await UploadTcxAsync(tokens, ride);
        await VerifyUploadAsync(retry);
    }

    static async Task<HttpResponseMessage> UploadTcxAsync(Tokens tokens, RideRecord ride)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + "/providers/sources/notebook/items/upload");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var form = new MultipartFormDataContent();
        request.Content = form;
        var file = new ByteArrayContent(TcxExporter.Create(ride));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        form.Add(file, "file", $"SuitoRide-{ride.Id:N}.tcx");
        return await Http.SendAsync(request);
    }

    static async Task VerifyUploadAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(UiText.Format($"FitnessSyncer odmítl nahrání (HTTP {(int)response.StatusCode})."));
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!result.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
            !items.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(id.GetString()) && id.GetString() != "-FILTERED-"))
            throw new InvalidOperationException(UiText.Get("FitnessSyncer nepotvrdil uložení jízdy. Zkontroluj její stav ve FitnessSynceru."));
    }

    static async Task<Tokens> RefreshAsync(Tokens tokens)
    {
        var renewed = await RequestTokensAsync(tokens.ClientId, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken
        });
        StoreTokens(renewed);
        return renewed;
    }

    static async Task<Tokens> RequestTokensAsync(string clientId, Dictionary<string, string> fields)
    {
        fields["client_id"] = clientId;
        fields["redirect_uri"] = Redirect.AbsoluteUri;
        using var form = new FormUrlEncodedContent(fields);
        using var response = await Http.PostAsync(Api + "/oauth/access_token", form);
        if (!response.IsSuccessStatusCode)
        {
            string help = fields["grant_type"] == "refresh_token"
                ? UiText.Get("Otevři nastavení FitnessSynceru a připoj účet znovu.")
                : UiText.Get("Spusť přihlášení znovu a vlož novou adresu z prohlížeče.");
            throw new InvalidOperationException(UiText.Format($"FitnessSyncer odmítl ověření (HTTP {(int)response.StatusCode}). {help}"));
        }
        var result = JsonSerializer.Deserialize<TokenResponse>(await response.Content.ReadAsStringAsync());
        if (result == null || string.IsNullOrWhiteSpace(result.Access) || string.IsNullOrWhiteSpace(result.Refresh) || result.Lifetime <= 0)
            throw new InvalidOperationException(UiText.Get("FitnessSyncer vrátil neúplné údaje o přihlášení."));
        if (result.Scope != null && !result.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(Scope))
            throw new InvalidOperationException(UiText.Get("FitnessSyncer nepovolil zápis aktivit do Notebooku."));
        return new(clientId, result.Access, result.Refresh, DateTimeOffset.UtcNow.AddSeconds(result.Lifetime));
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnected) return;
        var tokens = LoadTokens();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = tokens.RefreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = tokens.ClientId
        });
        using var response = await Http.PostAsync(Api + "/oauth/revoke_token", form);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(UiText.Format($"Odebrání přístupu FitnessSyncer nepotvrdil (HTTP {(int)response.StatusCode})."));
        File.Delete(TokenPath);
    }

    static Tokens LoadTokens()
    {
        if (!File.Exists(TokenPath)) throw new InvalidOperationException(UiText.Get("Nejprve propoj účet FitnessSyncer."));
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<Tokens>(plain)
                ?? throw new InvalidOperationException(UiText.Get("Připoj FitnessSyncer znovu."));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    static void StoreTokens(Tokens tokens)
    {
        Directory.CreateDirectory(Folder);
        string temp = Path.Combine(Folder, $"{Guid.NewGuid():N}.tmp");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try
        {
            File.WriteAllBytes(temp, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            File.Move(temp, TokenPath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
