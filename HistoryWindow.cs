using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

internal sealed class HistoryWindow : SuitoDialog
{
    readonly RideHistoryStore store;
    readonly StravaService strava=new();
    readonly DataGrid grid;
    readonly TextBlock summary,status;
    readonly Button send,connect,forget,export,routePhotos;
    public HistoryWindow(RideHistoryStore store)
    {
        this.store=store;
        Title=UiText.Get("Historie tréninků");Width=1000;Height=550;MinWidth=650;MinHeight=350;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new DockPanel{Margin=new Thickness(16)};
        summary=new TextBlock{Margin=new Thickness(0,0,0,8),TextWrapping=TextWrapping.Wrap};
        DockPanel.SetDock(summary,Dock.Top);panel.Children.Add(summary);
        var actions=new WrapPanel{Margin=new Thickness(0,0,0,8)};
        connect=new ActionButton(ButtonIcons.Connect, "Propojit Stravu", prompt: true){ Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,8,5)};
        send=new ActionButton(ButtonIcons.Upload, "Odeslat vybranou jízdu na Stravu", prompt: true){ Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,8,5)};
        export=new ActionButton(ButtonIcons.Save, "Uložit TCX", prompt: true){ Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,8,5)};
        routePhotos=new ActionButton(ButtonIcons.Panorama, "Fotky z trasy", prompt: true){ Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,8,5)};
        forget=new ActionButton(ButtonIcons.Disconnect, "Odebrat uložené spojení", prompt: true){ Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,8,5)};
        actions.Children.Add(connect);actions.Children.Add(send);actions.Children.Add(export);actions.Children.Add(routePhotos);actions.Children.Add(forget);
        DockPanel.SetDock(actions,Dock.Top);panel.Children.Add(actions);
        status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
        DockPanel.SetDock(status,Dock.Top);panel.Children.Add(status);
        grid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,
            HeadersVisibility=DataGridHeadersVisibility.Column,SelectionUnit=DataGridSelectionUnit.FullRow,SelectionMode=DataGridSelectionMode.Single};
        void Column(string name,string field,double width)=>grid.Columns.Add(new DataGridTextColumn
            {Header=name,Binding=new System.Windows.Data.Binding(field),Width=width});
        Column(UiText.Get("Datum"),"DateDisplay",135);Column(UiText.Get("Trasa"),"RouteName",180);Column(UiText.Get("Stav"),"ResultDisplay",110);
        Column(UiText.Get("Čas"),"DurationDisplay",90);Column(UiText.Get("Vzdálenost"),"DistanceDisplay",105);
        Column(UiText.Get("Prům. rychlost"),"AverageSpeedDisplay",120);Column(UiText.Get("Prům. výkon"),"PowerDisplay",100);
        try{
            var(rides,invalid)=store.Load();grid.ItemsSource=rides;
            summary.Text=rides.Count==0?UiText.Get("Zatím nemáš uložené žádné jízdy."):UiText.Format($"Uložené jízdy: {rides.Count} · celkem {rides.Sum(r=>r.DistanceMeters)/1000:F1} km");
            if(invalid>0)summary.Text+=UiText.Format($" Některé záznamy ({invalid}) se nepodařilo načíst.");
        }catch(Exception ex){summary.Text=UiText.Get("Historii se nepodařilo načíst: ")+ex.Message;}
        panel.Children.Add(grid);Content=panel;
        connect.Click+=(_,_)=>{new StravaConnectWindow(strava){Owner=this}.ShowDialog();RefreshStatus();};
        forget.Click+=(_,_)=>{
            if(MessageBox.Show(this,UiText.Get("Odebrat uložené propojení se Stravou z tohoto počítače?"),"Strava",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            try{strava.Forget();RefreshStatus();}catch(Exception ex){status.Text=UiText.Get("Spojení nelze odebrat: ")+ex.Message;}
        };
        send.Click+=SendRide;export.Click+=ExportRide;routePhotos.Click+=OpenRoutePhotos;
        RefreshStatus();
    }
    void RefreshStatus() => status.Text = UiText.Get(strava.IsConnected ? "Strava připojena." : "Strava nepřipojena.");

    void SetActionsEnabled(bool enabled) =>
        send.IsEnabled=connect.IsEnabled=forget.IsEnabled=export.IsEnabled=routePhotos.IsEnabled=enabled;
    void ExportRide(object s,RoutedEventArgs e)
    {
        if(grid.SelectedItem is not RideRecord ride){status.Text=UiText.Get("Nejprve vyber jízdu.");return;}
        var dlg=new SaveFileDialog{Filter=UiText.Get("Trénink TCX (*.tcx)|*.tcx"),DefaultExt=".tcx",AddExtension=true,FileName=$"SuitoRide-{ride.StartedAt:yyyy-MM-dd-HHmm}.tcx"};
        if(dlg.ShowDialog(this)!=true)return;
        try{File.WriteAllBytes(dlg.FileName,TcxExporter.Create(ride));status.Text=UiText.Get("Soubor TCX byl uložen.");}
        catch(Exception ex){status.Text=UiText.Get("TCX nelze uložit: ")+ex.Message;}
    }
    void OpenRoutePhotos(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not RideRecord ride)
        {
            status.Text = UiText.Get("Nejprve vyber jízdu.");
            return;
        }
        var folder = store.PhotoFolder(ride.Id);
        try
        {
            if (!Directory.Exists(folder) ||
                (!File.Exists(Path.Combine(folder, "midpoint.png")) && !Directory.EnumerateFiles(folder, "*.jpg").Any()))
            {
                status.Text = UiText.Get("Tato jízda nemá uložené fotky z trasy.");
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            status.Text = UiText.Get("Složku s fotkami se nepodařilo otevřít.");
        }
    }

    async void SendRide(object s,RoutedEventArgs e)
    {
        if(grid.SelectedItem is not RideRecord ride){status.Text=UiText.Get("Nejprve vyber jízdu.");return;}
        if(!strava.IsConnected){status.Text=UiText.Get("Nejprve propoj účet Strava.");return;}
        var details = new RideUploadWindow(UiText.Get("Odeslat vybranou jízdu na Stravu"), ride) { Owner = this };
        if (details.ShowDialog() != true) return;
        SetActionsEnabled(false);
        status.Text=UiText.Get("Odesílám jízdu a čekám na zpracování ve Stravě…");
        try{
            long id=await strava.SendAsync(ride, details.ActivityName, details.Description, CancellationToken.None);
            status.Text=UiText.Format($"Jízda je na Stravě uložená (aktivita {id}).");
        }catch(Exception ex){status.Text=UiText.Get("Jízdu nelze odeslat: ")+ex.Message;}
        finally{SetActionsEnabled(true);}
    }
}
