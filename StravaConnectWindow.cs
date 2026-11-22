using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

internal sealed class StravaConnectWindow : SuitoDialog
{
    readonly StravaService service;
    readonly TextBox clientId;
    readonly PasswordBox secret;
    readonly TextBlock status;
    readonly Button connect;
    readonly CancellationTokenSource cancellation=new();
    public StravaConnectWindow(StravaService service)
    {
        this.service=service;
        Title=UiText.Get("Propojení se Stravou");Width=590;Height=430;MinWidth=500;MinHeight=400;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(20)};
        panel.Children.Add(new TextBlock{Text=UiText.Get("Pro přímé propojení vytvoř vlastní API aplikaci ve Stravě (Strava k tomu vyžaduje předplatné). Autorizační doménu nastav na localhost. Client Secret nikomu neposílej."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
        var open=new Button{Content=UiText.Get("Otevřít nastavení Strava API"),HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,0,10)};
        open.Click+=(_,_)=>{try{Process.Start(new ProcessStartInfo("https://www.strava.com/settings/api"){UseShellExecute=true});}catch(Exception ex){MessageBox.Show(this,UiText.Get("Nelze otevřít prohlížeč: ")+ex.Message,"Strava");}};panel.Children.Add(open);
        panel.Children.Add(new TextBlock{Text="Client ID"});
        clientId=new TextBox{Margin=new Thickness(0,3,0,8)};panel.Children.Add(clientId);
        panel.Children.Add(new TextBlock{Text="Client Secret"});
        secret=new PasswordBox{Margin=new Thickness(0,3,0,10)};panel.Children.Add(secret);
        connect=new Button{Content=UiText.Get("Připojit Stravu"),Padding=new Thickness(12,6,12,6),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,10)};
        connect.Click+=Connect;panel.Children.Add(connect);
        status=new TextBlock{Text=service.IsConnected?UiText.Get("Účet je propojen. Pro změnu účtu zadej nové údaje."):UiText.Get("Přístup potvrdíš na webu Stravy. Údaje se uloží zašifrované pro tvůj účet Windows."),TextWrapping=TextWrapping.Wrap};
        panel.Children.Add(status);
        Closing+=(_,_)=>cancellation.Cancel();
        Content=panel;
    }
    async void Connect(object sender,RoutedEventArgs e)
    {
        connect.IsEnabled=false;status.Text=UiText.Get("Otevři prohlížeč a potvrď přístup ke Stravě…");
        try{
            await service.ConnectAsync(clientId.Text,secret.Password,cancellation.Token);
            secret.Clear();status.Text=UiText.Get("Účet je propojený. Můžeš okno zavřít a odeslat vybranou jízdu.");
        }catch(OperationCanceledException){status.Text=UiText.Get("Propojení bylo zrušeno nebo vypršel čas.");}
        catch(Exception ex){status.Text=UiText.Get("Propojení se nepodařilo: ")+ex.Message;}
        finally{connect.IsEnabled=true;}
    }
}
