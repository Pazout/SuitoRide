using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SuitoRide;

internal sealed class DropboxSettingsWindow : SuitoDialog
{
    public DropboxSettingsWindow(string? catalog, DropboxRideArchive archive, Action<string> setCatalog)
    {
        Title = UiText.Get("Složky Dropboxu");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        string? dropbox = DropboxRideArchive.FindDropboxFolder();
        var panel = new StackPanel { Margin = new Thickness(16) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var catalogPath = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 5) };
        var archivePath = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 5) };
        var pickCatalog = new Button { Content = UiText.Get("Vybrat složku GPX"), Padding = new Thickness(9, 5, 9, 5), HorizontalAlignment = HorizontalAlignment.Left };
        var pickArchive = new Button { Content = UiText.Get("Vybrat složku pro TCX"), Padding = new Thickness(9, 5, 9, 5) };
        var disable = new Button
        {
            Content = UiText.Get("Vypnout automatické ukládání"), Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(8, 0, 0, 0)
        };
        var actions = new WrapPanel();
        actions.Children.Add(pickArchive);
        actions.Children.Add(disable);
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("Vyber místní složky synchronizované aplikací Dropbox ve Windows."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock { Text = UiText.Get("Katalog GPX"), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(catalogPath);
        panel.Children.Add(pickCatalog);
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("Automatické ukládání jízd jako TCX"), FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0)
        });
        panel.Children.Add(archivePath);
        panel.Children.Add(actions);
        panel.Children.Add(status);
        panel.Children.Add(new Button
        {
            Content = UiText.Get("Zavřít"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 8, 0, 0)
        });
        Content = panel;

        string? destination = null;
        try { destination = archive.LoadFolder(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = ex.Message; }

        void UpdatePaths()
        {
            catalogPath.Text = catalog ?? UiText.Get("Složka zatím není vybrána.");
            archivePath.Text = destination ?? UiText.Get("Automatické ukládání je vypnuté.");
            disable.IsEnabled = destination != null;
        }

        void Choose(string title, string? previous, Action<string> apply)
        {
            var folder = new OpenFolderDialog { Title = title, Multiselect = false };
            if (Directory.Exists(previous)) folder.InitialDirectory = previous;
            if (folder.ShowDialog(this) != true) return;
            try
            {
                apply(folder.FolderName);
                status.Text = "";
                UpdatePaths();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = UiText.Get("Složku nelze uložit: ") + ex.Message;
            }
        }

        pickCatalog.Click += (_, _) => Choose(UiText.Get("Vyberte složku s GPX trasami"), dropbox ?? catalog, selected =>
        {
            setCatalog(selected);
            catalog = selected;
        });
        pickArchive.Click += (_, _) => Choose(UiText.Get("Vybrat složku pro TCX"), Directory.Exists(destination) ? destination : dropbox, selected =>
        {
            archive.SaveFolder(selected);
            destination = selected;
        });
        disable.Click += (_, _) =>
        {
            try
            {
                archive.SaveFolder(null);
                destination = null;
                status.Text = "";
                UpdatePaths();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = UiText.Get("Složku nelze uložit: ") + ex.Message;
            }
        };
        UpdatePaths();
    }
}
