using System.Windows;
using System.Windows.Media;

namespace SuitoRide;

public partial class MainWindow
{
    ReferenceRide? referenceRide;

    void OpenReferenceRide(object sender, RoutedEventArgs e)
    {
        if (riding || starting || ticking) return;
        if (route.Count < 2)
        {
            Status.Text = UiText.Get("Nejprve načti GPX trasu pro porovnání.");
            return;
        }
        var course = route.Select(p => new ReferenceRoutePoint(p.Lat, p.Lon, p.D)).ToArray();
        var window = new ReferenceRideWindow(historyStore, referenceRide, source => ReferenceRide.Match(source, course))
            { Owner = this };
        if (window.ShowDialog() == true)
        {
            referenceRide = window.SelectedReference;
            RefreshReferenceRide();
        }
    }

    void RefreshReferenceRide()
    {
        if (referenceRide == null)
        {
            if (ReferenceStatus.Visibility != Visibility.Collapsed)
                SendMap(new { type = "reference-clear" });
            ReferenceStatus.Visibility = Visibility.Collapsed;
            return;
        }

        ReferenceStatus.Visibility = Visibility.Visible;
        ReferenceStatus.ToolTip = UiText.Format($"Soupeř: {referenceRide.Name}");
        if (!riding)
        {
            ReferenceStatus.Text = (string)ReferenceStatus.ToolTip;
            ReferenceStatus.Foreground = Brushes.Plum;
            SendMap(new { type = "reference-clear" });
            return;
        }

        double elapsed = rideClock.Elapsed.TotalSeconds;
        long seconds = (long)Math.Round(referenceRide.TimeAt(distance) - elapsed, MidpointRounding.AwayFromZero);
        ReferenceStatus.Text = seconds switch
        {
            > 0 => UiText.Format($"{seconds} s napřed"),
            < 0 => UiText.Format($"{-seconds} s pozadu"),
            _ => UiText.Get("Stejný čas")
        };
        ReferenceStatus.Foreground = seconds switch
        {
            > 0 => Brushes.LightGreen,
            < 0 => Brushes.Orange,
            _ => Brushes.Plum
        };
        double meters = referenceRide.DistanceAt(elapsed);
        var position = PositionAt(meters);
        SendMap(new
        {
            type = "reference",
            lat = position.Lat,
            lon = position.Lon,
            name = UiText.Get("Virtuální soupeř")
        });
    }
}
