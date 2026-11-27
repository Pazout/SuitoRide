using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SuitoRide;

internal sealed class CountdownWindow : Window
{
    readonly TextBlock number, subtitle, telemetry, hint;
    readonly Action cancel;
    bool dismissing;

    public CountdownWindow(Action cancelCountdown)
    {
        cancel=cancelCountdown;
        Title=UiText.Get("Příprava na jízdu");
        WindowStyle=WindowStyle.None;
        AllowsTransparency=true;
        ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        WindowState=WindowState.Maximized;
        Background=new SolidColorBrush(Color.FromArgb(180,16,27,37));
        Foreground=Brushes.White;
        ShowInTaskbar=false;
        Topmost=true;

        var layout=new Grid { Margin=new Thickness(32) };
        layout.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var center=new StackPanel { VerticalAlignment=VerticalAlignment.Center, HorizontalAlignment=HorizontalAlignment.Center };
        center.Children.Add(new TextBlock { Text=UiText.Get("PŘIPRAV SE NA JÍZDU"), FontSize=28, FontWeight=FontWeights.SemiBold, Foreground=new SolidColorBrush(Color.FromRgb(150,215,197)), HorizontalAlignment=HorizontalAlignment.Center });
        number=new TextBlock { Text="—", FontSize=230, FontWeight=FontWeights.Bold, HorizontalAlignment=HorizontalAlignment.Center, TextAlignment=TextAlignment.Center };
        center.Children.Add(new Viewbox { Stretch=Stretch.Uniform, MaxHeight=320, Child=number });
        subtitle = new TextBlock { FontSize=24, TextAlignment=TextAlignment.Center };
        telemetry = new TextBlock { FontSize=22, TextAlignment=TextAlignment.Center, Margin=new Thickness(0,12,0,0) };
        hint = new TextBlock { FontSize=24, TextAlignment=TextAlignment.Center, TextWrapping=TextWrapping.Wrap, MaxWidth=780, Margin=new Thickness(0,16,0,0) };
        center.Children.Add(subtitle);
        center.Children.Add(telemetry);
        center.Children.Add(hint);
        UpdateStart(null, null, null, interrupted: false);
        Grid.SetRow(center,0);layout.Children.Add(center);

        var button=new ActionButton(ButtonIcons.Close, "Zrušit start") { Padding=new Thickness(28,14,28,14), FontSize=18,
            HorizontalAlignment=HorizontalAlignment.Center, Margin=new Thickness(0,0,0,32),
            Style=(Style)Application.Current.FindResource("HighContrastButton") };
        button.Click+=(_,_)=>cancel();
        Grid.SetRow(button,1);layout.Children.Add(button);
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){cancel();e.Handled=true;}};
        Closing+=(_,_)=>{if(!dismissing)cancel();};
        Content=layout;
    }

    public void PlaceOnMonitor(Rect bounds)
    {
        WindowState=WindowState.Normal;
        WindowStartupLocation=WindowStartupLocation.Manual;
        Left=bounds.Left;Top=bounds.Top;Width=bounds.Width;Height=bounds.Height;
    }

    public void UpdateStart(int? remaining, double? speed, double? cadence, bool interrupted)
    {
        number.Text = remaining?.ToString() ?? speed?.ToString("F1") ?? "—";
        subtitle.Text = remaining.HasValue ? UiText.Get("sekund do startu") : "km/h";

        string speedText = speed?.ToString("F1") ?? "—";
        string cadenceText = cadence?.ToString("F0") ?? "—";
        telemetry.Text = UiText.Format($"Rychlost {speedText} km/h  ·  Kadence {cadenceText} ot/min");
        hint.Text = remaining.HasValue ? "" : interrupted
            ? UiText.Format($"Odpočet přerušen. Rozjeď se znovu nad {RideStartCountdown.SpeedThresholdKmh} km/h.")
            : UiText.Format($"Rozjeď se nad {RideStartCountdown.SpeedThresholdKmh} km/h a pokračuj ve šlapání.");
    }

    public void ShowConnecting()
    {
        number.Text = "—";
        subtitle.Text = UiText.Get("Trenažér se ještě připojuje.");
        telemetry.Text = "";
        hint.Text = "";
    }

    public void ShowStarting()
    {
        number.Text = "0";
        subtitle.Text = UiText.Get("Zahajuji jízdu…");
        hint.Text = "";
    }

    public void Dismiss()
    {
        dismissing=true;
        if(IsVisible)Close();
    }
}
