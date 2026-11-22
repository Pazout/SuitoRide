using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

internal sealed class MapApiKeySettings : StackPanel
{
    sealed record Provider(string Code, string Name, string Instructions);

    public MapApiKeySettings(string? initialProvider)
    {
        Provider[] providers =
        [
            new("mapy", "Mapy.com", UiText.Get("Použij klíč pro Mapy.com REST API.")),
            new("google", "Google Street View", UiText.Get("Použij klíč pro Google Maps Embed API.")),
            new("google3d", "Google 3D", UiText.Get("Vlož klíč pro Maps JavaScript API s povolenou fakturací. Google 3D se načte, až zvolíš jeho pohled. Pokud omezuješ klíč podle webu, povol https://suitoride-3d.local/*."))
        ];
        var selector = new ComboBox
        {
            ItemsSource = providers,
            DisplayMemberPath = nameof(Provider.Name),
            SelectedItem = providers.FirstOrDefault(provider => provider.Code == initialProvider) ?? providers[0]
        };
        var instructions = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
        var input = new PasswordBox { Margin = new Thickness(0, 0, 0, 8), MaxLength = 256 };
        var save = new Button { Content = UiText.Get("Uložit klíč"), IsEnabled = false, Padding = new Thickness(9, 5, 9, 5) };
        var remove = new Button { Content = UiText.Get("Odstranit uložený klíč"),
            Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(9, 5, 9, 5) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
        var actions = new WrapPanel();
        actions.Children.Add(save);
        actions.Children.Add(remove);
        Children.Add(new Label { Content = UiText.Get("Klíče pro panoramata a 3D mapy"), Target = selector });
        Children.Add(selector);
        Children.Add(instructions);
        Children.Add(new Label { Content = UiText.Get("API klíč"), Target = input });
        Children.Add(input);
        Children.Add(actions);
        Children.Add(status);

        selector.SelectionChanged += (_, _) => RefreshProvider();
        input.PasswordChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(input.Password);
        save.Click += (_, _) =>
        {
            if (selector.SelectedItem is not Provider provider || string.IsNullOrWhiteSpace(input.Password)) return;
            try
            {
                PanoramaKeyStore.Save(provider.Code, input.Password.Trim());
                RefreshProvider();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                status.Text = UiText.Get("Klíč se nepodařilo uložit.");
            }
        };
        remove.Click += (_, _) =>
        {
            if (selector.SelectedItem is not Provider provider) return;
            try
            {
                PanoramaKeyStore.Delete(provider.Code);
                RefreshProvider();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = UiText.Get("Klíč se nepodařilo odstranit.");
            }
        };
        RefreshProvider();

        void RefreshProvider()
        {
            if (selector.SelectedItem is not Provider provider) return;
            input.Clear();
            instructions.Text = provider.Instructions;
            bool exists = PanoramaKeyStore.Exists(provider.Code);
            remove.IsEnabled = exists;
            status.Text = UiText.Get(exists ? "Klíč je uložený." : "Pro zvoleného poskytovatele zatím není uložen klíč.");
        }
    }
}
