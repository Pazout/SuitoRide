using System.Windows;
using System.Windows.Controls;

namespace SuitoRide;

internal sealed class RideUploadWindow : SuitoDialog
{
    readonly TextBox name;
    readonly TextBox description;

    public string ActivityName => name.Text.Trim();
    public string Description => description.Text.Trim();

    public RideUploadWindow(string title, RideRecord ride)
    {
        Title = title;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(16) };
        name = new TextBox { Text = $"Suito Ride: {ride.RouteName}", Margin = new Thickness(0, 2, 0, 8) };
        description = new TextBox
        {
            MinHeight = 90, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 2, 0, 10)
        };
        var send = new ActionButton(ButtonIcons.Send, "Odeslat") { IsDefault = true, Padding = new Thickness(9, 5, 9, 5) };
        var cancel = new ActionButton(ButtonIcons.Close, "Zrušit")
        {
            IsCancel = true, Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(8, 0, 0, 0)
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(send);
        actions.Children.Add(cancel);

        panel.Children.Add(new Label { Content = UiText.Get("Název aktivity"), Target = name });
        panel.Children.Add(name);
        panel.Children.Add(new Label { Content = UiText.Get("Popis aktivity (nepovinný)"), Target = description });
        panel.Children.Add(description);
        panel.Children.Add(actions);
        Content = panel;

        name.TextChanged += (_, _) => send.IsEnabled = ActivityName.Length > 0;
        send.Click += (_, _) => DialogResult = true;
        Loaded += (_, _) => name.Focus();
    }
}
