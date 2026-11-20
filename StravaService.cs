using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuitoRide;

internal sealed record StravaCredentials(string ClientId,string ClientSecret,string AccessToken,string RefreshToken,long ExpiresAt,long AthleteId);
internal sealed record StravaReceipt(string State,long? UploadId=null,long? ActivityId=null);

internal sealed class StravaService
{
    readonly string folder;
    readonly HttpClient http;
    readonly Action<string> openBrowser;
    readonly SemaphoreSlim operation=new(1,1);
    public StravaService(string? dataFolder=null,HttpClient? client=null,Action<string>? browser=null)
    {
        folder=dataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SuitoRide","strava");
        http=client ?? new HttpClient { Timeout=TimeSpan.FromSeconds(30) };
        openBrowser=browser ?? (url=>{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});});
    }
    string AuthPath=>Path.Combine(folder,"authorization.dat");
    public bool IsConnected=>File.Exists(AuthPath);
    static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    void WriteAtomic(string path,byte[] bytes)
    {
        Directory.CreateDirectory(folder);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.WriteAllBytes(temp,bytes);File.Move(temp,path,true); }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    void SaveCredentials(StravaCredentials auth)
    {
        byte[] plain=JsonSerializer.SerializeToUtf8Bytes(auth);
        try{WriteAtomic(AuthPath,ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser));}
        finally{CryptographicOperations.ZeroMemory(plain);}
    }
    StravaCredentials LoadCredentials()
    {
        if(!File.Exists(AuthPath))throw new InvalidOperationException("Nejprve propoj účet Strava.");
        byte[] plain=ProtectedData.Unprotect(File.ReadAllBytes(AuthPath),null,DataProtectionScope.CurrentUser);
        try{return JsonSerializer.Deserialize<StravaCredentials>(plain,JsonOptions) ?? throw new InvalidDataException("Uložené přihlášení je neplatné.");}
        finally{CryptographicOperations.ZeroMemory(plain);}
    }
    public void Forget() { if(File.Exists(AuthPath))File.Delete(AuthPath); }
    static int FreePort()
    {
        var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();
        try{return ((IPEndPoint)socket.LocalEndpoint).Port;}finally{socket.Stop();}
    }
    public async Task ConnectAsync(string clientId,string secret,CancellationToken token)
    {
        clientId=clientId.Trim();secret=secret.Trim();
        if(!long.TryParse(clientId,out long id)||id<=0 || string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Zadej platné Client ID a Client Secret ze Stravy.");
        string state=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        int port=FreePort();string callback=$"http://127.0.0.1:{port}/callback/";
        using var listener=new HttpListener();
        listener.Prefixes.Add(callback);listener.Start();
        string auth="https://www.strava.com/oauth/authorize?"+
            $"client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(callback)}"+
            $"&response_type=code&approval_prompt=auto&scope={Uri.EscapeDataString("activity:write")}&state={state}";
        openBrowser(auth);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while(true)
        {
            var context=await listener.GetContextAsync().WaitAsync(timeout.Token);
            var query=context.Request.QueryString;
            bool valid=string.Equals(query["state"],state,StringComparison.Ordinal);
            string? code=query["code"],scope=query["scope"],error=query["error"];
            string response=valid ? "Odpověď byla přijata. Vrať se do aplikace Suito Ride." : "Neplatná odpověď. Vrať se do aplikace.";
            byte[] body=Encoding.UTF8.GetBytes(response);
            context.Response.StatusCode=valid?200:400;context.Response.ContentType="text/plain; charset=utf-8";
            context.Response.ContentLength64=body.Length;
            try{await context.Response.OutputStream.WriteAsync(body,timeout.Token);}finally{context.Response.Close();}
            if(!valid)continue;
            if(error!=null)throw new InvalidOperationException("Přístup ke Stravě nebyl povolen.");
            if(string.IsNullOrEmpty(code) || !(scope??"").Split(',', ' ').Contains("activity:write"))
                throw new InvalidOperationException("Strava neudělila oprávnění k zápisu aktivit.");
            using var form=new FormUrlEncodedContent(new Dictionary<string,string>{
                ["client_id"]=clientId,["client_secret"]=secret,["code"]=code,["grant_type"]="authorization_code"});
            using var result=await http.PostAsync("https://www.strava.com/oauth/token",form,timeout.Token);
            if(!result.IsSuccessStatusCode)throw new InvalidOperationException($"Strava nepřijala propojení (HTTP {(int)result.StatusCode}).");
            using var json=JsonDocument.Parse(await result.Content.ReadAsStringAsync(timeout.Token));
            var root=json.RootElement;
            long athlete=root.GetProperty("athlete").GetProperty("id").GetInt64();
            var credentials=new StravaCredentials(clientId,secret,root.GetProperty("access_token").GetString()!,
                root.GetProperty("refresh_token").GetString()!,root.GetProperty("expires_at").GetInt64(),athlete);
            SaveCredentials(credentials);
            return;
        }
    }
    async Task<StravaCredentials> ValidCredentials(CancellationToken token)
    {
        var auth=LoadCredentials();
        if(auth.ExpiresAt>DateTimeOffset.UtcNow.ToUnixTimeSeconds()+300)return auth;
        using var form=new FormUrlEncodedContent(new Dictionary<string,string>{
            ["client_id"]=auth.ClientId,["client_secret"]=auth.ClientSecret,
            ["refresh_token"]=auth.RefreshToken,["grant_type"]="refresh_token"});
        using var result=await http.PostAsync("https://www.strava.com/oauth/token",form,token);
        if(!result.IsSuccessStatusCode)throw new InvalidOperationException($"Strava neobnovila přístup (HTTP {(int)result.StatusCode}). Zkus účet znovu propojit.");
        using var json=JsonDocument.Parse(await result.Content.ReadAsStringAsync(token));var root=json.RootElement;
        auth=auth with{AccessToken=root.GetProperty("access_token").GetString()!,RefreshToken=root.GetProperty("refresh_token").GetString()!,ExpiresAt=root.GetProperty("expires_at").GetInt64()};
        SaveCredentials(auth);
        return auth;
    }
    string ReceiptPath(long athlete,Guid ride)=>Path.Combine(folder,$"{athlete}-{ride:N}.json");
    StravaReceipt? GetReceipt(long athlete,Guid ride)
    {
        string path=ReceiptPath(athlete,ride);
        return File.Exists(path)?JsonSerializer.Deserialize<StravaReceipt>(File.ReadAllText(path),JsonOptions):null;
    }
    void SetReceipt(long athlete,Guid ride,StravaReceipt receipt)=>WriteAtomic(ReceiptPath(athlete,ride),JsonSerializer.SerializeToUtf8Bytes(receipt));
    public async Task<long> SendAsync(RideRecord ride,CancellationToken token)
    {
        await operation.WaitAsync(token);
        try {
            var auth=await ValidCredentials(token);
            var receipt=GetReceipt(auth.AthleteId,ride.Id);
            if(receipt?.ActivityId is long existing)return existing;
            if(receipt?.State=="sending")
                throw new InvalidOperationException("Výsledek předchozího odeslání není známý. Nejdřív ověř aktivitu ve Stravě, aby nevznikla duplicita.");
            long uploadId;
            if(receipt?.UploadId is long pending)uploadId=pending;
            else {
                byte[] tcx=TcxExporter.Create(ride);
                using var form=new MultipartFormDataContent();
                form.Add(new ByteArrayContent(tcx),"file",$"suitoride-{ride.Id:N}.tcx");
                form.Add(new StringContent("tcx"),"data_type");
                form.Add(new StringContent("VirtualRide"),"sport_type");
                form.Add(new StringContent("1"),"trainer");
                form.Add(new StringContent($"Suito Ride: {ride.RouteName}"),"name");
                form.Add(new StringContent($"suitoride-{ride.Id:N}"),"external_id");
                using var request=new HttpRequestMessage(HttpMethod.Post,"https://www.strava.com/api/v3/uploads");
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",auth.AccessToken);
                request.Content=form;
                SetReceipt(auth.AthleteId,ride.Id,new("sending"));
                HttpResponseMessage result;
                try{result=await http.SendAsync(request,token);}
                catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException)
                {throw new InvalidOperationException("Nepodařilo se ověřit výsledek odeslání. Nejdřív zkontroluj Stravu, než jízdu odešleš znovu.",ex);}
                using(result)
                {
                    if(!result.IsSuccessStatusCode)
                    {
                        if((int)result.StatusCode>=400 && (int)result.StatusCode<500 && result.StatusCode!=HttpStatusCode.Conflict)
                            File.Delete(ReceiptPath(auth.AthleteId,ride.Id));
                        throw new InvalidOperationException($"Strava nahrání nepřijala (HTTP {(int)result.StatusCode}).");
                    }
                    using var json=JsonDocument.Parse(await result.Content.ReadAsStringAsync(token));
                    uploadId=json.RootElement.GetProperty("id").GetInt64();
                    SetReceipt(auth.AthleteId,ride.Id,new("pending",uploadId));
                }
            }
            for(int i=0;i<30;i++)
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,$"https://www.strava.com/api/v3/uploads/{uploadId}");
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",auth.AccessToken);
                using var response=await http.SendAsync(request,token);
                if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"Stav nahrávání nelze ověřit (HTTP {(int)response.StatusCode}).");
                using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var root=json.RootElement;
                if(root.TryGetProperty("activity_id",out var a)&&a.ValueKind==JsonValueKind.Number)
                {
                    long activity=a.GetInt64();SetReceipt(auth.AthleteId,ride.Id,new("complete",uploadId,activity));return activity;
                }
                if(root.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(error.GetString()))
                    throw new InvalidOperationException("Strava aktivitu nezpracovala: "+error.GetString());
                await Task.Delay(1000,token);
            }
            throw new InvalidOperationException("Strava jízdu ještě zpracovává. Stav můžeš ověřit opětovným stiskem tlačítka.");
        }finally{operation.Release();}
    }
}
