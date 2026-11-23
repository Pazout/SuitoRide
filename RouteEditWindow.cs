using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SuitoRide;

internal sealed class RouteEditWindow : SuitoDialog
{
    readonly Slider start, end;
    readonly TextBlock description;
    readonly CheckBox reverse;
    readonly Canvas profile;
    readonly ElevationProfile elevationProfile;
    readonly double totalDistance;
    bool dragging;
    bool dragLeft;
    public double StartMeters => start.Value;
    public double EndMeters => end.Value;
    public bool Reverse => reverse.IsChecked==true;

    public RouteEditWindow(double lengthMeters, ElevationProfile elevationProfile)
    {
        totalDistance=lengthMeters;
        this.elevationProfile=elevationProfile;
        Title=UiText.Get("Upravit trasu"); Width=680; Height=490; MinWidth=520; MinHeight=470;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel { Margin=new Thickness(20) };
        panel.Children.Add(new TextBlock { Text=UiText.Get("Přetáhni značky na výškovém profilu nebo použij posuvníky."), TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,8) });
        profile=new Canvas { Height=140, Background=new SolidColorBrush(Color.FromRgb(28,44,56)), ClipToBounds=true, Cursor=Cursors.SizeWE, Margin=new Thickness(0,0,0,12) };
        panel.Children.Add(profile);
        panel.Children.Add(new TextBlock { Text=UiText.Get("Levý okraj úseku") });
        start=new Slider { Minimum=0, Maximum=lengthMeters, Value=0, Margin=new Thickness(0,0,0,8) };
        panel.Children.Add(start);
        panel.Children.Add(new TextBlock { Text=UiText.Get("Pravý okraj úseku") });
        end=new Slider { Minimum=0, Maximum=lengthMeters, Value=lengthMeters, Margin=new Thickness(0,0,0,8) };
        panel.Children.Add(end);
        description=new TextBlock { Margin=new Thickness(0,0,0,8), TextWrapping=TextWrapping.Wrap };
        panel.Children.Add(description);
        reverse=new CheckBox { Content=UiText.Get("Obrátit směr vybraného úseku"), Margin=new Thickness(0,0,0,12) };
        panel.Children.Add(reverse);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right };
        var cancel=new Button { Content=UiText.Get("Zrušit"), MinWidth=95, Margin=new Thickness(0,0,8,0), IsCancel=true };
        var apply=new Button { Content=UiText.Get("Použít úsek"), MinWidth=105, IsDefault=true };
        apply.Click+=(_,_)=>{
            if(EndMeters-StartMeters<10) { MessageBox.Show(this,UiText.Get("Cíl musí být alespoň 10 metrů za začátkem výběru."),UiText.Get("Upravit trasu"),MessageBoxButton.OK,MessageBoxImage.Information);return; }
            DialogResult=true;
        };
        buttons.Children.Add(cancel);buttons.Children.Add(apply);panel.Children.Add(buttons);
        start.ValueChanged+=(_,_)=>{if(start.Value>end.Value-10)start.Value=Math.Max(0,end.Value-10);UpdateDescription();};
        end.ValueChanged+=(_,_)=>{if(end.Value<start.Value+10)end.Value=Math.Min(totalDistance,start.Value+10);UpdateDescription();};
        reverse.Checked+=(_,_)=>UpdateDescription();reverse.Unchecked+=(_,_)=>UpdateDescription();
        profile.SizeChanged+=(_,_)=>DrawProfile();
        profile.MouseLeftButtonDown+=ProfileMouseDown;
        profile.MouseMove+=ProfileMouseMove;
        profile.MouseLeftButtonUp+=(_,_)=>{dragging=false;profile.ReleaseMouseCapture();};
        profile.LostMouseCapture+=(_,_)=>dragging=false;
        UpdateDescription();Content=panel;
    }
    void UpdateDescription()
    {
        double from=Reverse?EndMeters:StartMeters,to=Reverse?StartMeters:EndMeters;
        description.Text=UiText.Format($"Start: {from/1000:F2} km    Cíl: {to/1000:F2} km    Délka: {Math.Max(0,EndMeters-StartMeters)/1000:F2} km");
        DrawProfile();
    }
    double Coordinate(double distance) => 10+(profile.ActualWidth-20)*distance/totalDistance;
    double DistanceAt(double x) => Math.Clamp((x-10)/Math.Max(1,profile.ActualWidth-20),0,1)*totalDistance;
    void ProfileMouseDown(object sender,MouseButtonEventArgs e)
    {
        if(profile.ActualWidth<=20)return;
        double x=e.GetPosition(profile).X;
        dragLeft=Math.Abs(x-Coordinate(StartMeters))<=Math.Abs(x-Coordinate(EndMeters));
        dragging=true;profile.CaptureMouse();MoveMarker(x);e.Handled=true;
    }
    void ProfileMouseMove(object sender,MouseEventArgs e)
    {
        if(dragging && e.LeftButton==MouseButtonState.Pressed)MoveMarker(e.GetPosition(profile).X);
    }
    void MoveMarker(double x)
    {
        double value=DistanceAt(x);
        if(dragLeft)start.Value=Math.Clamp(value,0,Math.Max(0,EndMeters-10));
        else end.Value=Math.Clamp(value,Math.Min(totalDistance,StartMeters+10),totalDistance);
    }
    void DrawProfile()
    {
        profile.Children.Clear();
        if(profile.ActualWidth<=20 || totalDistance<=0)return;
        double width=profile.ActualWidth,height=profile.ActualHeight;
        var points=elevationProfile.Sample(0,totalDistance,width-20);
        double min=points.Min(p=>p.Elevation),max=points.Max(p=>p.Elevation),span=Math.Max(1,max-min);
        double left=Coordinate(StartMeters),right=Coordinate(EndMeters);
        var selected=new Rectangle { Width=Math.Max(0,right-left),Height=height,Fill=new SolidColorBrush(Color.FromArgb(50,60,210,170)),IsHitTestVisible=false };
        Canvas.SetLeft(selected,left);profile.Children.Add(selected);
        var line=new Polyline { Stroke=Brushes.MediumAquamarine,StrokeThickness=2,IsHitTestVisible=false };
        foreach(var p in points)line.Points.Add(new Point(Coordinate(p.Distance),18+(height-32)*(1-(p.Elevation-min)/span)));
        profile.Children.Add(line);
        AddMarker(left,Reverse?UiText.Get("CÍL"):"START",Brushes.Gold,height,width);
        AddMarker(right,Reverse?"START":UiText.Get("CÍL"),Brushes.White,height,width);
    }
    void AddMarker(double x,string label,Brush brush,double height,double width)
    {
        profile.Children.Add(new Line { X1=x,X2=x,Y1=0,Y2=height,Stroke=brush,StrokeThickness=3,IsHitTestVisible=false });
        var text=new TextBlock { Text=label,Foreground=brush,FontWeight=FontWeights.Bold,FontSize=11,IsHitTestVisible=false };
        Canvas.SetLeft(text,Math.Clamp(x+4,0,Math.Max(0,width-45)));Canvas.SetTop(text,2);profile.Children.Add(text);
    }
}
