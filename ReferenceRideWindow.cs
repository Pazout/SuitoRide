using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Xml;

namespace SuitoRide;

internal sealed class ReferenceRideWindow : SuitoDialog
{
    readonly Func<ReferenceSource, ReferenceRide> match;
    readonly ListBox history;
    readonly ActionButton useHistory, loadTcx, clear;
    readonly TextBlock status;
    bool busy;

    sealed record HistoryChoice(RideRecord Ride)
    {
        public string Label => $"{Ride.RouteName}   ·   {Ride.DateDisplay}   ·   {Ride.DurationDisplay}   ·   {Ride.DistanceDisplay}";
    }

    public ReferenceRide? SelectedReference { get; private set; }

    public ReferenceRideWindow(RideHistoryStore store, ReferenceRide? current, Func<ReferenceSource, ReferenceRide> match)
    {
        this.match = match;

        Title = UiText.Get("Referenční jízda");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 570;
        Height = 420;
        MinWidth = 420;
        MinHeight = 330;

        var panel = new DockPanel { Margin = new Thickness(16) };
        var intro = new TextBlock
        {
            Text = UiText.Get("Vyber jízdu ze stejné trasy a směru. S referencí jízda začíná od startu GPX."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(intro, Dock.Top);
        panel.Children.Add(intro);

        history = new ListBox { DisplayMemberPath = nameof(HistoryChoice.Label) };
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        useHistory = Button(ButtonIcons.Check, "Použít vybranou jízdu", () =>
        {
            if (history.SelectedItem is HistoryChoice choice)
                _ = SelectAsync(() => ReferenceSource.FromHistory(choice.Ride));
        });
        loadTcx = Button(ButtonIcons.OpenFolder, "Načíst TCX", () =>
        {
            var open = new OpenFileDialog { Filter = UiText.Get("Trénink TCX (*.tcx)|*.tcx") };
            if (open.ShowDialog(this) == true) _ = SelectAsync(() => ReferenceSource.FromTcx(open.FileName));
        }, prompt: true);
        clear = Button(ButtonIcons.Close, "Odebrat referenci", () => { DialogResult = true; });
        clear.IsEnabled = current != null;
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);

        status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(status);
        panel.Children.Add(history);
        Content = panel;
        history.SelectionChanged += (_, _) => useHistory.IsEnabled = !busy && history.SelectedItem != null;
        useHistory.IsEnabled = false;

        try
        {
            history.ItemsSource = store.Load().Rides.Where(ride => ride.Samples?.Any(s => s.Position != null) == true)
                .Select(ride => new HistoryChoice(ride)).ToArray();
            if (history.Items.Count == 0) status.Text = UiText.Get("V historii zatím není jízda s GPS záznamem.");
            else if (current != null) status.Text = UiText.Format($"Soupeř: {current.Name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Text = UiText.Get("Historii se nepodařilo načíst.");
        }

        ActionButton Button(System.Windows.Media.Geometry icon, string title, Action action, bool prompt = false)
        {
            var button = new ActionButton(icon, title, prompt) { Margin = new Thickness(0, 0, 6, 4) };
            button.Click += (_, _) => action();
            actions.Children.Add(button);
            return button;
        }
    }

    async Task SelectAsync(Func<ReferenceSource> load)
    {
        bool canClear = clear.IsEnabled;
        busy = true;
        history.IsEnabled = useHistory.IsEnabled = loadTcx.IsEnabled = clear.IsEnabled = false;
        status.Text = UiText.Get("Ověřuji referenční jízdu…");
        try
        {
            var reference = await Task.Run(() => match(load()));
            if (!IsLoaded) return;
            SelectedReference = reference;
            DialogResult = true;
        }
        catch (InvalidDataException ex)
        {
            status.Text = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
        {
            status.Text = UiText.Get("Referenční jízdu se nepodařilo načíst.");
        }
        finally
        {
            busy = false;
            history.IsEnabled = loadTcx.IsEnabled = true;
            clear.IsEnabled = canClear;
            useHistory.IsEnabled = history.SelectedItem != null;
        }
    }
}
