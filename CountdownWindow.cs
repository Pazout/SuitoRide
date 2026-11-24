using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SuitoRide;

internal sealed class CountdownWindow : Window
{
    readonly TextBlock number;
    readonly Action cancel;
    bool dismissing;

    public CountdownWindow(int initialSeconds,Action cancelCountdown)
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
        number=new TextBlock { Text=initialSeconds.ToString(), FontSize=230, FontWeight=FontWeights.Bold, HorizontalAlignment=HorizontalAlignment.Center, TextAlignment=TextAlignment.Center };
        center.Children.Add(new Viewbox { Stretch=Stretch.Uniform, MaxHeight=320, Child=number });
        center.Children.Add(new TextBlock { Text=UiText.Get("sekund do startu"), FontSize=24, HorizontalAlignment=HorizontalAlignment.Center });
        Grid.SetRow(center,0);layout.Children.Add(center);

        var button=new ActionButton(ButtonIcons.Close, "Zrušit odpočet") { Padding=new Thickness(28,14,28,14), FontSize=18,
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

    public void SetRemaining(int remaining) => number.Text=remaining.ToString();
    public void Dismiss()
    {
        dismissing=true;
        if(IsVisible)Close();
    }
}
