using Microsoft.AspNetCore.SignalR.Client;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

public sealed record SharedRider(string Id,string Name,double Distance,bool Riding);

internal sealed class SharedRideWindow : SuitoDialog
{
    readonly TextBox nameBox, addressBox, roomBox;
    readonly TextBlock stateText;
    readonly Button hostButton,remoteHostButton,joinButton,disconnectButton,copyButton;
    readonly string routeHash;
    readonly Action<SharedRider> updateRider;
    readonly Action<string> removeRider;
    readonly Action clearRiders;
    readonly Action connected;
    HubConnection? connection;
    Process? server;
    bool allowClose,connecting;
    public bool Connected => connection?.State==HubConnectionState.Connected;
    public bool IsBusy => Connected || connecting;

    public SharedRideWindow(string hash,Action<SharedRider> update,Action<string> remove,Action clear,Action onConnected)
    {
        routeHash=hash;updateRider=update;removeRider=remove;clearRiders=clear;connected=onConnected;
        Title=UiText.Get("Společná vyjížďka");Width=530;Height=480;MinWidth=480;MinHeight=460;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(20)};
        panel.Children.Add(new TextBlock{Text=UiText.Get("Oba jezdci musí načíst stejnou GPX trasu. Spouštějte jízdu každý zvlášť."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
        panel.Children.Add(new TextBlock{Text=UiText.Get("Tvoje jméno")});
        nameBox=new TextBox{Text=Environment.UserName,MaxLength=30,Margin=new Thickness(0,3,0,10)};panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock{Text=UiText.Get("Adresa serveru (pro připojení kamaráda)")});
        addressBox=new TextBox{Text="http://127.0.0.1:5077",Margin=new Thickness(0,3,0,10)};panel.Children.Add(addressBox);
        panel.Children.Add(new TextBlock{Text=UiText.Get("Kód místnosti")});
        roomBox=new TextBox{MaxLength=32,Margin=new Thickness(0,3,0,10)};panel.Children.Add(roomBox);
        var buttons=new WrapPanel();
        hostButton=new Button{Content=UiText.Get("Vytvořit místnost"),Padding=new Thickness(9,6,9,6),Margin=new Thickness(0,0,8,8)};
        remoteHostButton=new Button{Content=UiText.Get("Vytvořit na serveru"),Padding=new Thickness(9,6,9,6),Margin=new Thickness(0,0,8,8)};
        joinButton=new Button{Content=UiText.Get("Připojit se"),Padding=new Thickness(9,6,9,6),Margin=new Thickness(0,0,8,8)};
        disconnectButton=new Button{Content=UiText.Get("Odpojit"),Padding=new Thickness(9,6,9,6),Margin=new Thickness(0,0,8,8),IsEnabled=false};
        copyButton=new Button{Content=UiText.Get("Kopírovat kód"),Padding=new Thickness(9,6,9,6),Margin=new Thickness(0,0,8,8)};
        buttons.Children.Add(hostButton);buttons.Children.Add(remoteHostButton);buttons.Children.Add(joinButton);buttons.Children.Add(disconnectButton);buttons.Children.Add(copyButton);panel.Children.Add(buttons);
        stateText=new TextBlock{Text=UiText.Get("Pro internet použijte společnou zabezpečenou síť (VPN). Host sdělí kamarádovi svou VPN adresu a kód místnosti."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0)};panel.Children.Add(stateText);
        hostButton.Click+=async (_,_)=>await Connect(true,true);
        remoteHostButton.Click+=async (_,_)=>await Connect(true,false);
        joinButton.Click+=async (_,_)=>await Connect(false,false);
        disconnectButton.Click+=async (_,_)=>await Disconnect();
        copyButton.Click+=(_,_)=>{if(!string.IsNullOrWhiteSpace(roomBox.Text))Clipboard.SetText(roomBox.Text.Trim());};
        Closing+=(_,e)=>{if(!allowClose){e.Cancel=true;Hide();}};
        Content=panel;
    }
    static bool IsAllowed(Uri uri)
    {
        if(uri.Scheme==Uri.UriSchemeHttps)return true;
        if(uri.Scheme!=Uri.UriSchemeHttp)return false;
        if(uri.IsLoopback)return true;
        if(!IPAddress.TryParse(uri.Host,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork)return false;
        var b=ip.GetAddressBytes();
        return b[0]==10 || (b[0]==172 && b[1]>=16 && b[1]<=31) || (b[0]==192 && b[1]==168) || (b[0]==100 && b[1]>=64 && b[1]<=127);
    }
    async Task Connect(bool create,bool localHost)
    {
        if(connecting||Connected)return;
        string name=nameBox.Text.Trim();
        if(name.Length==0||name.Length>30||name.Any(char.IsControl)){stateText.Text=UiText.Get("Zadej jméno o délce 1–30 znaků.");return;}
        string room=create?Convert.ToHexString(RandomNumberGenerator.GetBytes(16)):roomBox.Text.Trim().ToUpperInvariant();
        if(room.Length!=32||room.Any(c=>!Uri.IsHexDigit(c))){stateText.Text=UiText.Get("Zadej 32znakový kód místnosti.");return;}
        if(!Uri.TryCreate(localHost?"http://127.0.0.1:5077":addressBox.Text.Trim(),UriKind.Absolute,out var address)||!IsAllowed(address))
        {stateText.Text=UiText.Get("Použij HTTPS nebo adresu počítače v soukromé či VPN síti.");return;}
        connecting=true;hostButton.IsEnabled=remoteHostButton.IsEnabled=joinButton.IsEnabled=false;stateText.Text=UiText.Get("Připojuji…");
        try{
            if(localHost){
                string exe=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(SharedRideWindow).Assembly.Location)!,"server","SpolecnaJizda.Server.exe");
                if(!System.IO.File.Exists(exe))throw new Exception(UiText.Get("Chybí server společné jízdy ve složce aplikace."));
                server=Process.Start(new ProcessStartInfo{FileName=exe,Arguments="--urls http://0.0.0.0:5077",WorkingDirectory=System.IO.Path.GetDirectoryName(exe)!,UseShellExecute=false,CreateNoWindow=true})
                    ?? throw new Exception(UiText.Get("Server se nepodařilo spustit."));
            }
            var endpoint=new Uri(address.ToString().TrimEnd('/')+"/ride");
            var hub=new HubConnectionBuilder().WithUrl(endpoint).Build();
            connection=hub;
            hub.On<SharedRider>("RiderUpdated",r=>Dispatcher.BeginInvoke(()=>updateRider(r)));
            hub.On<string>("RiderLeft",id=>Dispatcher.BeginInvoke(()=>removeRider(id)));
            hub.Closed+=ex=>{Dispatcher.BeginInvoke(async ()=>{if(!ReferenceEquals(connection,hub))return;await DisconnectCore();stateText.Text=ex==null?UiText.Get("Spojení ukončeno."):UiText.Get("Spojení se přerušilo.");hostButton.IsEnabled=remoteHostButton.IsEnabled=joinButton.IsEnabled=true;disconnectButton.IsEnabled=false;});return Task.CompletedTask;};
            if(localHost){
                Exception? last=null;
                for(int attempt=0;attempt<20;attempt++){
                    if(server==null || server.HasExited)throw new Exception(UiText.Get("Server nelze spustit. Port 5077 může být obsazen."));
                    try{await hub.StartAsync();last=null;break;}catch(Exception ex){last=ex;await Task.Delay(250);}
                }
                if(last!=null)throw last;
            }else await hub.StartAsync();
            await hub.InvokeAsync("Join",room,routeHash,name,create);
            roomBox.Text=room;disconnectButton.IsEnabled=true;connected();
            stateText.Text=localHost?UiText.Get("Místnost vytvořena. Pošli kamarádovi kód a svou VPN adresu s portem 5077."):create?UiText.Get("Místnost vytvořena. Pošli kamarádovi adresu serveru a kód."):UiText.Get("Připojeno ke společné jízdě.");
        }catch(Exception ex){
            await DisconnectCore();
            stateText.Text=UiText.Get("Připojení selhalo: ")+ex.Message;
        }finally{connecting=false;hostButton.IsEnabled=remoteHostButton.IsEnabled=joinButton.IsEnabled=!Connected;}
    }
    bool sending,pendingUpdate;
    double latestDistance;
    bool latestRiding;
    public async void Publish(double distance,bool riding)
    {
        if(connection?.State!=HubConnectionState.Connected)return;
        latestDistance=distance;latestRiding=riding;pendingUpdate=true;
        if(sending)return;
        sending=true;
        try{
            while(pendingUpdate){
                pendingUpdate=false;
                var hub=connection;
                if(hub?.State!=HubConnectionState.Connected)return;
                await hub.InvokeAsync("Update",latestDistance,latestRiding);
            }
        }catch(Exception){ }
        finally{sending=false;}
    }
    public async Task Disconnect()
    {
        await DisconnectCore();stateText.Text=UiText.Get("Odpojeno.");hostButton.IsEnabled=remoteHostButton.IsEnabled=joinButton.IsEnabled=true;disconnectButton.IsEnabled=false;
    }
    async Task DisconnectCore()
    {
        var hub=connection;connection=null;pendingUpdate=false;
        if(hub!=null)try{await hub.DisposeAsync();}catch{}
        if(server!=null){try{if(!server.HasExited)server.Kill();server.Dispose();}catch{}server=null;}
        clearRiders();
    }
    public void Shutdown(){allowClose=true;var hub=connection;connection=null;if(server!=null){try{if(!server.HasExited)server.Kill();server.Dispose();}catch{}server=null;}if(hub!=null)_=hub.DisposeAsync();try{Close();}catch(InvalidOperationException){}}
}
