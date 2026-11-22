using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

internal sealed class FitnessSyncerConnectWindow : SuitoDialog
{
    public FitnessSyncerConnectWindow(FitnessSyncerService service)
    {
        Title = "Připojit FitnessSyncer";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(16) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var clientId = new TextBox { Margin = new Thickness(0, 2, 0, 10) };

        var start = new Button { Content = "Otevřít přihlášení v prohlížeči", Padding = new Thickness(9, 5, 9, 5) };
        var callback = new TextBox { IsEnabled = false, Margin = new Thickness(0, 2, 0, 8) };
        var finish = new Button { Content = "Dokončit připojení", IsEnabled = false, Padding = new Thickness(9, 5, 9, 5) };
        var disconnect = new Button { Content = "Odpojit FitnessSyncer", IsEnabled = service.IsConnected, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(8, 0, 0, 0) };
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        actions.Children.Add(finish);
        actions.Children.Add(disconnect);

        panel.Children.Add(new Label { Content = "ID veřejného klienta", Target = clientId });
        panel.Children.Add(clientId);
        panel.Children.Add(start);
        panel.Children.Add(new TextBlock
        {
            Text = "Po potvrzení přístupu zkopíruj z adresního řádku celou výslednou adresu personal.fitnesssyncer.com a vlož ji sem.",
            Margin = new Thickness(0, 8, 0, 2), TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(callback);
        panel.Children.Add(actions);
        panel.Children.Add(status);
        Content = panel;

        FitnessSyncerService.Authorization? authorization = null;
        bool busy = false;
        Closing += (_, e) => e.Cancel = busy;

        start.Click += (_, _) =>
        {
            try
            {
                var pending = service.BeginAuthorization(clientId.Text);
                Process.Start(new ProcessStartInfo(pending.Url.AbsoluteUri) { UseShellExecute = true });
                authorization = pending;
                callback.Clear();
                callback.IsEnabled = finish.IsEnabled = true;
                callback.Focus();
                status.Text = "Čekám na adresu z prohlížeče.";
            }
            catch (Exception ex) { status.Text = "Přihlášení nelze otevřít: " + ex.Message; }
        };
        finish.Click += async (_, _) =>
        {
            if (authorization == null) return;
            SetBusy(true);
            try
            {
                await service.ConnectAsync(authorization, callback.Text);
                callback.Clear();
                SetBusy(false);
                DialogResult = true;
            }
            catch (Exception ex) { status.Text = "Připojení se nezdařilo: " + ex.Message; }
            finally { if (busy) SetBusy(false); }
        };
        disconnect.Click += async (_, _) =>
        {
            SetBusy(true);
            try
            {
                await service.DisconnectAsync();
                status.Text = "FitnessSyncer odpojen.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { SetBusy(false); }
        };

        void SetBusy(bool value)
        {
            busy = value;
            start.IsEnabled = clientId.IsEnabled = !busy;
            callback.IsEnabled = finish.IsEnabled = !busy && authorization != null;
            disconnect.IsEnabled = !busy && service.IsConnected;
        }
    }
}
