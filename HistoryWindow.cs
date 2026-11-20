using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

internal sealed class HistoryWindow : Window
{
    public HistoryWindow(RideHistoryStore store)
    {
        Title="Historie tréninků"; Width=900; Height=500; MinWidth=600; MinHeight=300;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new DockPanel { Margin=new Thickness(16) };
        var summary=new TextBlock { Margin=new Thickness(0,0,0,10), TextWrapping=TextWrapping.Wrap };
        DockPanel.SetDock(summary,Dock.Top); panel.Children.Add(summary);
        var grid=new DataGrid { AutoGenerateColumns=false, IsReadOnly=true, CanUserAddRows=false,
            HeadersVisibility=DataGridHeadersVisibility.Column, SelectionUnit=DataGridSelectionUnit.FullRow };
        void Column(string name,string field,double width) => grid.Columns.Add(new DataGridTextColumn
            { Header=name, Binding=new System.Windows.Data.Binding(field), Width=width });
        Column("Datum","DateDisplay",135);Column("Trasa","RouteName",180);Column("Stav","Result",110);
        Column("Čas","DurationDisplay",90);Column("Vzdálenost","DistanceDisplay",105);
        Column("Prům. rychlost","AverageSpeedDisplay",120);Column("Prům. výkon","PowerDisplay",100);
        try {
            var (rides,invalid)=store.Load();grid.ItemsSource=rides;
            summary.Text=rides.Count==0 ? "Zatím nemáš uložené žádné jízdy." : $"Uložené jízdy: {rides.Count} · celkem {rides.Sum(r=>r.DistanceMeters)/1000:F1} km";
            if(invalid>0)summary.Text+=$"  Některé záznamy ({invalid}) se nepodařilo načíst.";
        } catch(Exception ex) { summary.Text="Historii se nepodařilo načíst: "+ex.Message; }
        panel.Children.Add(grid); Content=panel;
    }
}
