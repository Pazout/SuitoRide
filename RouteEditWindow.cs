using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

internal sealed class RouteEditWindow : Window
{
    readonly Slider start, end;
    readonly TextBlock description;
    readonly CheckBox reverse;
    public double StartMeters => start.Value;
    public double EndMeters => end.Value;
    public bool Reverse => reverse.IsChecked==true;

    public RouteEditWindow(double lengthMeters)
    {
        Title="Upravit trasu"; Width=580; Height=340; MinWidth=500; MinHeight=320;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel { Margin=new Thickness(22) };
        panel.Children.Add(new TextBlock { Text="Vyber začátek a konec podle vzdálenosti v aktuální trase.", TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,12) });
        panel.Children.Add(new TextBlock { Text="Začátek" });
        start=new Slider { Minimum=0, Maximum=lengthMeters, Value=0, Margin=new Thickness(0,0,0,10) };
        panel.Children.Add(start);
        panel.Children.Add(new TextBlock { Text="Cíl" });
        end=new Slider { Minimum=0, Maximum=lengthMeters, Value=lengthMeters, Margin=new Thickness(0,0,0,10) };
        panel.Children.Add(end);
        description=new TextBlock { Margin=new Thickness(0,0,0,8) };
        panel.Children.Add(description);
        reverse=new CheckBox { Content="Obrátit směr vybraného úseku", Margin=new Thickness(0,0,0,15) };
        panel.Children.Add(reverse);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right };
        var cancel=new Button { Content="Zrušit", MinWidth=95, Margin=new Thickness(0,0,8,0), IsCancel=true };
        var apply=new Button { Content="Použít úsek", MinWidth=105, IsDefault=true };
        apply.Click+=(_,_)=>{
            if(EndMeters-StartMeters<10) { MessageBox.Show(this,"Cíl musí být alespoň 10 metrů za začátkem.","Upravit trasu",MessageBoxButton.OK,MessageBoxImage.Information);return; }
            DialogResult=true;
        };
        buttons.Children.Add(cancel);buttons.Children.Add(apply);panel.Children.Add(buttons);
        start.ValueChanged+=(_,_)=>UpdateDescription();
        end.ValueChanged+=(_,_)=>UpdateDescription();
        UpdateDescription(); Content=panel;
    }
    void UpdateDescription() => description.Text=$"Začátek: {StartMeters/1000:F2} km    Cíl: {EndMeters/1000:F2} km    Délka: {Math.Max(0,EndMeters-StartMeters)/1000:F2} km";
}
