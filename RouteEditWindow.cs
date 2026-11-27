using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SuitoRide;

internal sealed class RouteEditWindow : SuitoDialog
{
    internal const int DefaultSmoothingIterations = 31;
    readonly Slider start, end, smoothing;
    readonly CheckBox compressTrack;
    readonly TextBox toleranceInput, distanceInput, secondsInput;
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
    public int SmoothingIterations => (int)smoothing.Value;
    public bool Compress => compressTrack.IsChecked == true;
    public double CompressionTolerance { get; private set; }
    public double? MaxDistance { get; private set; }
    public double? MaxSeconds { get; private set; }

    public RouteEditWindow(double lengthMeters, ElevationProfile elevationProfile, int smoothingIterations)
    {
        totalDistance=lengthMeters;
        this.elevationProfile=elevationProfile;
        Title=UiText.Get("Upravit trasu"); Width=680; Height=680; MinWidth=520; MinHeight=440;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(20) };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
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
        var smoothOptions = new DockPanel { Margin = new Thickness(0, 0, 12, 12) };
        string intensity = UiText.Get("Intenzita vyhlazení");
        var intensityLabel = new TextBlock { Text = intensity, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        smoothOptions.Children.Add(intensityLabel);
        smoothing = new Slider { Minimum = 1, Maximum = 61, TickFrequency = 5, IsSnapToTickEnabled = true, Value = smoothingIterations };
        System.Windows.Automation.AutomationProperties.SetName(smoothing, intensity);
        var currentIntensity = new TextBlock { Width = 26, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        currentIntensity.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Slider.Value)) { Source = smoothing });
        DockPanel.SetDock(currentIntensity, Dock.Right);
        smoothOptions.Children.Add(currentIntensity);
        smoothOptions.Children.Add(smoothing);
        panel.Children.Add(smoothOptions);

        compressTrack = new CheckBox { Content = UiText.Get("Zmenšit počet bodů (Compress)"), Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(compressTrack);
        var compressionOptions = new StackPanel { Margin = new Thickness(18, 0, 0, 12), IsEnabled = false };
        var compressionInputs = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        toleranceInput = AddNumberInput(compressionInputs, "Tolerance odchylky (m)", "4");
        distanceInput = AddNumberInput(compressionInputs, "Max. rozestup (m)", "");
        secondsInput = AddNumberInput(compressionInputs, "Max. časový odstup (s)", "");
        compressionOptions.Children.Add(compressionInputs);
        compressionOptions.Children.Add(new TextBlock
        {
            Text = UiText.Get("Prázdný limit se nepoužije. Časový odstup vyžaduje časy v GPX."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        compressTrack.Checked += (_, _) => compressionOptions.IsEnabled = true;
        compressTrack.Unchecked += (_, _) => compressionOptions.IsEnabled = false;
        panel.Children.Add(compressionOptions);

        var buttons=new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right };
        var cancel=new ActionButton(ButtonIcons.Close, "Zrušit") { MinWidth=95, Margin=new Thickness(0,0,8,0), IsCancel=true };
        var apply=new ActionButton(ButtonIcons.Check, "Použít změny", prompt: true) { MinWidth=105, IsDefault=true };
        apply.Click+=(_,_)=>{
            if(EndMeters-StartMeters<10) { MessageBox.Show(this,UiText.Get("Cíl musí být alespoň 10 metrů za začátkem výběru."),UiText.Get("Upravit trasu"),MessageBoxButton.OK,MessageBoxImage.Information);return; }
            if (ReadCompressionOptions()) DialogResult=true;
        };
        buttons.Children.Add(cancel);buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel });
        start.ValueChanged+=(_,_)=>{if(start.Value>end.Value-10)start.Value=Math.Max(0,end.Value-10);UpdateDescription();};
        end.ValueChanged+=(_,_)=>{if(end.Value<start.Value+10)end.Value=Math.Min(totalDistance,start.Value+10);UpdateDescription();};
        reverse.Checked+=(_,_)=>UpdateDescription();reverse.Unchecked+=(_,_)=>UpdateDescription();
        profile.SizeChanged+=(_,_)=>DrawProfile();
        profile.MouseLeftButtonDown+=ProfileMouseDown;
        profile.MouseMove+=ProfileMouseMove;
        profile.MouseLeftButtonUp+=(_,_)=>{dragging=false;profile.ReleaseMouseCapture();};
        profile.LostMouseCapture+=(_,_)=>dragging=false;
        UpdateDescription();Content=layout;
    }
    static TextBox AddNumberInput(Panel parent, string label, string initialValue)
    {
        var column = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        string caption = UiText.Get(label);
        column.Children.Add(new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = initialValue };
        System.Windows.Automation.AutomationProperties.SetName(input, caption);
        column.Children.Add(input);
        parent.Children.Add(column);
        return input;
    }

    static bool ReadPositiveNumber(TextBox input, bool optional, out double? value)
    {
        value = null;
        if (optional && string.IsNullOrWhiteSpace(input.Text)) return true;
        if (!double.TryParse(input.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture, out double number) ||
            !double.IsFinite(number) || number <= 0) return false;
        value = number;
        return true;
    }

    bool ReadCompressionOptions()
    {
        if (!Compress) return true;
        if (!ReadPositiveNumber(toleranceInput, false, out var tolerance) ||
            !ReadPositiveNumber(distanceInput, true, out var distance) ||
            !ReadPositiveNumber(secondsInput, true, out var seconds))
        {
            MessageBox.Show(this, UiText.Get("Zadej kladnou toleranci a kladné nebo prázdné limity."),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        CompressionTolerance = tolerance!.Value;
        MaxDistance = distance;
        MaxSeconds = seconds;
        return true;
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
