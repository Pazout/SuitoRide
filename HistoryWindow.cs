using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

internal sealed class HistoryWindow : Window
{
    readonly RideHistoryStore store;
    readonly StravaService strava=new();
    readonly DataGrid grid;
    readonly TextBlock summary,status;
    readonly Button send,connect,forget,export;
    public HistoryWindow(RideHistoryStore store)
    {
        this.store=store;
        Title="Historie tréninků";Width=1000;Height=550;MinWidth=650;MinHeight=350;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new DockPanel{Margin=new Thickness(16)};
        summary=new TextBlock{Margin=new Thickness(0,0,0,8),TextWrapping=TextWrapping.Wrap};
        DockPanel.SetDock(summary,Dock.Top);panel.Children.Add(summary);
        var actions=new WrapPanel{Margin=new Thickness(0,0,0,8)};
        connect=new Button{Content="Propojit Stravu",Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,8,5)};
        send=new Button{Content="Odeslat vybranou jízdu na Stravu",Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,8,5)};
        export=new Button{Content="Uložit TCX",Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,8,5)};
        forget=new Button{Content="Odebrat uložené spojení",Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,8,5)};
        actions.Children.Add(connect);actions.Children.Add(send);actions.Children.Add(export);actions.Children.Add(forget);
        DockPanel.SetDock(actions,Dock.Top);panel.Children.Add(actions);
        status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
        DockPanel.SetDock(status,Dock.Top);panel.Children.Add(status);
        grid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,
            HeadersVisibility=DataGridHeadersVisibility.Column,SelectionUnit=DataGridSelectionUnit.FullRow,SelectionMode=DataGridSelectionMode.Single};
        void Column(string name,string field,double width)=>grid.Columns.Add(new DataGridTextColumn
            {Header=name,Binding=new System.Windows.Data.Binding(field),Width=width});
        Column("Datum","DateDisplay",135);Column("Trasa","RouteName",180);Column("Stav","Result",110);
        Column("Čas","DurationDisplay",90);Column("Vzdálenost","DistanceDisplay",105);
        Column("Prům. rychlost","AverageSpeedDisplay",120);Column("Prům. výkon","PowerDisplay",100);
        try{
            var(rides,invalid)=store.Load();grid.ItemsSource=rides;
            summary.Text=rides.Count==0?"Zatím nemáš uložené žádné jízdy.":$"Uložené jízdy: {rides.Count} · celkem {rides.Sum(r=>r.DistanceMeters)/1000:F1} km";
            if(invalid>0)summary.Text+=$" Některé záznamy ({invalid}) se nepodařilo načíst.";
        }catch(Exception ex){summary.Text="Historii se nepodařilo načíst: "+ex.Message;}
        panel.Children.Add(grid);Content=panel;
        connect.Click+=(_,_)=>{new StravaConnectWindow(strava){Owner=this}.ShowDialog();RefreshStatus();};
        forget.Click+=(_,_)=>{
            if(MessageBox.Show(this,"Odebrat uložené propojení se Stravou z tohoto počítače?","Strava",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            try{strava.Forget();RefreshStatus();}catch(Exception ex){status.Text="Spojení nelze odebrat: "+ex.Message;}
        };
        send.Click+=SendRide;export.Click+=ExportRide;
        RefreshStatus();
    }
    void RefreshStatus()=>status.Text=strava.IsConnected
        ?"Propojení se Stravou je uložené; platnost se ověří při odeslání. Soukromí aktivity se řídí nastavením Stravy."
        :"Strava není propojená. Můžeš ji propojit nebo uložit jízdu do souboru TCX.";
    void ExportRide(object s,RoutedEventArgs e)
    {
        if(grid.SelectedItem is not RideRecord ride){status.Text="Nejprve vyber jízdu.";return;}
        var dlg=new SaveFileDialog{Filter="Trénink TCX (*.tcx)|*.tcx",DefaultExt=".tcx",AddExtension=true,FileName=$"SuitoRide-{ride.StartedAt:yyyy-MM-dd-HHmm}.tcx"};
        if(dlg.ShowDialog(this)!=true)return;
        try{File.WriteAllBytes(dlg.FileName,TcxExporter.Create(ride));status.Text="Soubor TCX byl uložen.";}
        catch(Exception ex){status.Text="TCX nelze uložit: "+ex.Message;}
    }
    async void SendRide(object s,RoutedEventArgs e)
    {
        if(grid.SelectedItem is not RideRecord ride){status.Text="Nejprve vyber jízdu.";return;}
        if(!strava.IsConnected){status.Text="Nejprve propoj účet Strava.";return;}
        send.IsEnabled=connect.IsEnabled=forget.IsEnabled=export.IsEnabled=false;
        status.Text="Odesílám jízdu a čekám na zpracování ve Stravě…";
        try{
            long id=await strava.SendAsync(ride,CancellationToken.None);
            status.Text=$"Jízda je na Stravě uložená (aktivita {id}).";
        }catch(Exception ex){status.Text="Jízdu nelze odeslat: "+ex.Message;}
        finally{send.IsEnabled=connect.IsEnabled=forget.IsEnabled=export.IsEnabled=true;}
    }
}
