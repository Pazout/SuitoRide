using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

internal sealed class GitHubUpdateAccessWindow : SuitoDialog
{
    public string Token { get; private set; } = "";

    public GitHubUpdateAccessWindow()
    {
        Title = "Přístup k aktualizacím na GitHubu";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Repozitář Pazout/SuitoRide je soukromý. Použij token s přístupem k tomuto repozitáři a oprávněním Contents: Read. Po ověření se uloží zašifrovaně pro tvůj účet Windows.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        var input = new PasswordBox { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new Label { Content = "Token GitHub", Target = input });
        panel.Children.Add(input);
        var actions = new WrapPanel();
        var open = new Button { Content = "Otevřít nastavení tokenů", Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 8, 0) };
        var verify = new Button { Content = "Ověřit přístup", IsDefault = true, IsEnabled = false, Padding = new Thickness(9, 5, 9, 5) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(open);
        actions.Children.Add(verify);
        panel.Children.Add(actions);
        panel.Children.Add(status);
        Content = panel;
        Loaded += (_, _) => input.Focus();
        input.PasswordChanged += (_, _) => verify.IsEnabled = !string.IsNullOrWhiteSpace(input.Password);
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/settings/personal-access-tokens") { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "Nastavení GitHubu nelze otevřít: " + ex.Message; }
        };
        verify.Click += (_, _) => { Token = input.Password.Trim(); input.Clear(); DialogResult = true; };
    }
}
