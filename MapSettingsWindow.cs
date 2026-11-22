using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace SuitoRide;

internal sealed class MapSettingsWindow : SuitoDialog
{
    internal static IReadOnlyList<int> SpacingOptions { get; } = [10, 20, 40, 60, 100];
    readonly CoreWebView2Profile? profile;
    readonly string? cachePath;
    readonly TextBlock cacheStatus = new() { TextWrapping=TextWrapping.Wrap };

    public MapSettingsWindow(CoreWebView2Profile? profile, int spacing, Action<int> saveSpacing, string riderName, Action<string> saveRiderName, string? keyProvider = null)
    {
        this.profile=profile;
        cachePath=profile==null ? null : Path.Combine(profile.ProfilePath,"Cache");
        Title=UiText.Get("Nastavení");
        Width=550;
        SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;

        var panel=new StackPanel { Margin=new Thickness(16) };
        panel.Children.Add(CreateLanguageSettings());
        panel.Children.Add(CreateRiderNameSettings(riderName,saveRiderName));
        panel.Children.Add(new Separator { Margin=new Thickness(0,8,0,8) });
        panel.Children.Add(new MapApiKeySettings(keyProvider));
        panel.Children.Add(new Separator { Margin=new Thickness(0,8,0,8) });
        var spacingRow=new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(0,0,0,8) };
        var label=new Label { Content=UiText.Get("Rozestup snímků (m):"), VerticalAlignment=VerticalAlignment.Center };
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
                settingsStatus.Text=UiText.Get("Rozestup uložen.");
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                settingsStatus.Text=UiText.Get("Rozestup nelze uložit: ")+ex.Message;
                spacingBox.SelectedItem=spacing;
            }
        };
        panel.Children.Add(new TextBlock { Text=UiText.Get("Disková mezipaměť map a panoramat"), FontWeight=FontWeights.SemiBold });
        panel.Children.Add(cacheStatus);
        panel.Children.Add(new TextBox { Text=cachePath ?? "", IsReadOnly=true, TextWrapping=TextWrapping.Wrap,
            Margin=new Thickness(0,6,0,8) });

        var actions=new WrapPanel();
        var open=new Button { Content=UiText.Get("Otevřít složku"), IsEnabled=profile!=null, Padding=new Thickness(9,5,9,5), Margin=new Thickness(0,0,8,0) };
        var clear=new Button { Content=UiText.Get("Vyčistit mezipaměť"), IsEnabled=profile!=null, Padding=new Thickness(9,5,9,5) };
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
                await UpdateCacheSizeAsync(UiText.Get("Vyčištěno. "));
            }
            catch(Exception ex){cacheStatus.Text=UiText.Get("Mezipaměť nelze vyčistit: ")+ex.Message;}
            finally{clear.IsEnabled=true;}
        };
    }

    static StackPanel CreateRiderNameSettings(string currentName, Action<string> saveName)
    {
        var panel=new StackPanel { Margin=new Thickness(0,6,0,0) };
        var row=new DockPanel();
        var nameBox=new TextBox { Text=currentName, MaxLength=30, VerticalContentAlignment=VerticalAlignment.Center };
        var label=new Label { Content=UiText.Get("Jméno jezdce na mapě"), Target=nameBox, VerticalAlignment=VerticalAlignment.Center };
        var save=new Button { Content=UiText.Get("Uložit jméno"), Margin=new Thickness(8,0,0,0), IsEnabled=false };
        var status=new TextBlock { TextWrapping=TextWrapping.Wrap };
        DockPanel.SetDock(save,Dock.Right);
        row.Children.Add(label);
        row.Children.Add(save);
        row.Children.Add(nameBox);
        panel.Children.Add(row);
        panel.Children.Add(status);

        nameBox.TextChanged+=(_,_)=>save.IsEnabled=nameBox.Text.Trim()!=currentName;
        save.Click+=(_,_)=>{
            string name=nameBox.Text.Trim();
            try
            {
                saveName(name);
                currentName=name;
                nameBox.Text=name;
                save.IsEnabled=false;
                status.Text=UiText.Get("Jméno jezdce uloženo.");
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                status.Text=UiText.Get("Jméno jezdce se nepodařilo uložit.");
            }
        };
        return panel;
    }

    static StackPanel CreateLanguageSettings()
    {
        var choices = new[] { new UiText.LanguageChoice("", UiText.Get("Podle regionálního nastavení Windows")) }
            .Concat(UiText.LanguageChoices).ToArray();
        string saved = UiText.ReadPreference();
        var panel = new StackPanel();
        var selector = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(UiText.LanguageChoice.Name),
            SelectedValuePath = nameof(UiText.LanguageChoice.Code),
            SelectedValue = saved
        };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
        panel.Children.Add(new Label { Content = UiText.Get("Jazyk aplikace"), Target = selector });
        panel.Children.Add(selector);
        panel.Children.Add(status);
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedValue is not string selected || selected == saved) return;
            try
            {
                UiText.SavePreference(selected);
                saved = selected;
                ShowPendingLanguage();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = UiText.Get("Jazyk se nepodařilo uložit.");
                selector.SelectedValue = saved;
            }
        };
        ShowPendingLanguage();
        return panel;

        void ShowPendingLanguage()
        {
            var selectedLanguage=UiText.ResolveLanguage(System.Globalization.CultureInfo.CurrentCulture,saved);
            status.Text=selectedLanguage!=UiText.Language ? UiText.Get("Jazyk se změní po restartu aplikace.") : "";
        }
    }

    async Task UpdateCacheSizeAsync(string prefix="")
    {
        if(cachePath==null)
        {
            cacheStatus.Text=UiText.Get("Webové zobrazení ještě není připravené.");
            return;
        }
        cacheStatus.Text=UiText.Get("Zjišťuji velikost…");
        try
        {
            long size=await Task.Run(()=>MeasureCache(cachePath));
            cacheStatus.Text=UiText.Format($"{prefix}Přibližná velikost: {size/1048576.0:N2} MiB");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            cacheStatus.Text=prefix+UiText.Get("Velikost mezipaměti nelze zjistit.");
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
        catch(Exception ex){cacheStatus.Text=UiText.Get("Složku nelze otevřít: ")+ex.Message;}
    }
}
