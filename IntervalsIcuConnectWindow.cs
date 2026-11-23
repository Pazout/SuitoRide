using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

internal sealed class IntervalsIcuConnectWindow : SuitoDialog
{
    public IntervalsIcuConnectWindow(IntervalsIcuService service)
    {
        Title = UiText.Get("Nastavit Intervals.icu");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(16) };
        var key = new PasswordBox { Margin = new Thickness(0, 2, 0, 10) };
        var settings = new Button { Content = UiText.Get("Otevřít nastavení Intervals.icu"), Padding = new Thickness(9, 5, 9, 5) };
        var save = new Button { Content = UiText.Get("Ověřit a uložit"), IsDefault = true, Padding = new Thickness(9, 5, 9, 5) };
        var forget = new Button
        {
            Content = UiText.Get("Odebrat uložené spojení"), IsEnabled = service.IsConnected,
            Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(8, 0, 0, 0)
        };
        var cancel = new Button
        {
            Content = UiText.Get("Zrušit"), IsCancel = true, Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(8, 0, 0, 0)
        };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var actions = new WrapPanel();
        actions.Children.Add(save);
        actions.Children.Add(forget);
        actions.Children.Add(cancel);

        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("V nastavení svého účtu Intervals.icu najdi sekci Developer Settings a vlož osobní API klíč."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(settings);
        panel.Children.Add(new Label { Content = UiText.Get("API klíč"), Target = key });
        panel.Children.Add(key);
        panel.Children.Add(actions);
        panel.Children.Add(status);
        Content = panel;
        Loaded += (_, _) => key.Focus();

        bool busy = false;
        Closing += (_, e) => e.Cancel = busy;
        settings.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://intervals.icu/settings") { UseShellExecute = true }); }
            catch { status.Text = UiText.Get("Nastavení Intervals.icu se nepodařilo otevřít."); }
        };
        save.Click += async (_, _) =>
        {
            busy = true;
            key.IsEnabled = save.IsEnabled = forget.IsEnabled = cancel.IsEnabled = false;
            try
            {
                await service.ConnectAsync(key.Password);
                key.Clear();
                busy = false;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                status.Text = UiText.Get("Připojení se nezdařilo: ") + ex.Message;
            }
            finally
            {
                busy = false;
                key.IsEnabled = save.IsEnabled = cancel.IsEnabled = true;
                forget.IsEnabled = service.IsConnected;
            }
        };
        forget.Click += (_, _) =>
        {
            try { service.Forget(); DialogResult = true; }
            catch (Exception ex) { status.Text = UiText.Get("Spojení nelze odebrat: ") + ex.Message; }
        };
    }
}
