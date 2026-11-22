using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace SuitoRide;

internal sealed class MapSettingsWindow : SuitoDialog
{
    internal static IReadOnlyList<int> SpacingOptions { get; } = [20, 40, 60, 100];
    readonly CoreWebView2Profile? profile;
    readonly string? cachePath;
    readonly TextBlock cacheStatus = new() { TextWrapping=TextWrapping.Wrap };

    public MapSettingsWindow(CoreWebView2Profile? profile, int spacing, Action<int> saveSpacing)
    {
        this.profile=profile;
        cachePath=profile==null ? null : Path.Combine(profile.ProfilePath,"Cache");
        Title="Cache a panorama";
        Width=550;
        SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;

        var panel=new StackPanel { Margin=new Thickness(16) };
        var spacingRow=new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(0,0,0,8) };
        var label=new Label { Content="Rozestup snímků (m):", VerticalAlignment=VerticalAlignment.Center };
        var spacingBox=new ComboBox { ItemsSource=SpacingOptions, SelectedItem=spacing, Width=85, VerticalAlignment=VerticalAlignment.Center };
        label.Target=spacingBox;
        spacingRow.Children.Add(label);
        spacingRow.Children.Add(spacingBox);
        panel.Children.Add(spacingRow);

        var settingsStatus=new TextBlock { TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,12) };
        panel.Children.Add(settingsStatus);
        spacingBox.SelectionChanged+=(_,_)=>{
            if(spacingBox.SelectedItem is not int selected || selected==spacing)return;
            try
            {
                saveSpacing(selected);
                spacing=selected;
                settingsStatus.Text="Rozestup uložen.";
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                settingsStatus.Text="Rozestup nelze uložit: "+ex.Message;
                spacingBox.SelectedItem=spacing;
            }
        };
        panel.Children.Add(new TextBlock { Text="Disková mezipaměť map a panoramat", FontWeight=FontWeights.SemiBold });
        panel.Children.Add(cacheStatus);
        panel.Children.Add(new TextBox { Text=cachePath ?? "", IsReadOnly=true, TextWrapping=TextWrapping.Wrap,
            Margin=new Thickness(0,6,0,8) });

        var actions=new WrapPanel();
        var open=new Button { Content="Otevřít složku", IsEnabled=profile!=null, Padding=new Thickness(9,5,9,5), Margin=new Thickness(0,0,8,0) };
        var clear=new Button { Content="Vyčistit mezipaměť", IsEnabled=profile!=null, Padding=new Thickness(9,5,9,5) };
        actions.Children.Add(open);
        actions.Children.Add(clear);
        panel.Children.Add(actions);
        Content=panel;
        Loaded+=async (_,_)=>await UpdateCacheSizeAsync();
        open.Click+=(_,_)=>OpenCacheFolder();
        clear.Click+=async (_,_)=>{
            if(profile==null)return;
            clear.IsEnabled=false;
            try
            {
                await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
                await UpdateCacheSizeAsync("Vyčištěno. ");
            }
            catch(Exception ex){cacheStatus.Text="Mezipaměť nelze vyčistit: "+ex.Message;}
            finally{clear.IsEnabled=true;}
        };
    }

    async Task UpdateCacheSizeAsync(string prefix="")
    {
        if(cachePath==null)
        {
            cacheStatus.Text="Webové zobrazení ještě není připravené.";
            return;
        }
        cacheStatus.Text="Zjišťuji velikost…";
        try
        {
            long size=await Task.Run(()=>MeasureCache(cachePath));
            cacheStatus.Text=$"{prefix}Přibližná velikost: {size/1048576.0:N2} MiB";
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            cacheStatus.Text=prefix+"Velikost mezipaměti nelze zjistit.";
        }
    }

    static long MeasureCache(string path)
    {
        if(!Directory.Exists(path))return 0;
        var options=new EnumerationOptions { RecurseSubdirectories=true, IgnoreInaccessible=true,
            AttributesToSkip=FileAttributes.ReparsePoint };
        long bytes=0;
        foreach(var file in new DirectoryInfo(path).EnumerateFiles("*",options))
        {
            try{bytes+=file.Length;}
            catch(FileNotFoundException){} // WebView2 může mezitím položku odstranit.
        }
        return bytes;
    }

    void OpenCacheFolder()
    {
        if(profile==null || cachePath==null)return;
        try
        {
            string folder=Directory.Exists(cachePath) ? cachePath : profile.ProfilePath;
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute=true });
        }
        catch(Exception ex){cacheStatus.Text="Složku nelze otevřít: "+ex.Message;}
    }
}
