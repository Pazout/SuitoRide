using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

internal sealed class Google3DKeyWindow : SuitoDialog
{
    public Google3DKeyWindow()
    {
        Title = "Klíč Google 3D Maps";
        Width = 530;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Vlož klíč pro Maps JavaScript API s povolenou fakturací. Google 3D se načte, až zvolíš jeho pohled. Pokud omezuješ klíč podle webu, povol https://suitoride-3d.local/*.",
            TextWrapping = TextWrapping.Wrap
        });
        var input = new PasswordBox { Margin = new Thickness(0, 4, 0, 8), MaxLength = 256 };
        panel.Children.Add(new Label { Content = "API klíč", Target = input });
        panel.Children.Add(input);
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var save = new Button { Content = "Uložit klíč", IsDefault = true, IsEnabled = false, Padding = new Thickness(9, 5, 9, 5) };
        var remove = new Button { Content = "Odstranit uložený klíč", IsEnabled = PanoramaKeyStore.Exists("google3d"),
            Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(9, 5, 9, 5) };
        row.Children.Add(save);
        row.Children.Add(remove);
        panel.Children.Add(row);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);
        Content = panel;
        input.PasswordChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(input.Password);
        save.Click += (_, _) =>
        {
            try
            {
                PanoramaKeyStore.Save("google3d", input.Password.Trim());
                input.Clear();
                DialogResult = true;
            }
            catch { status.Text = "Klíč se nepodařilo uložit."; }
        };
        remove.Click += (_, _) =>
        {
            try
            {
                PanoramaKeyStore.Delete("google3d");
                DialogResult = true;
            }
            catch { status.Text = "Klíč se nepodařilo odstranit."; }
        };
        Loaded += (_, _) => input.Focus();
    }
}
