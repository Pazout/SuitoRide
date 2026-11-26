using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace SuitoRide;

public partial class MainWindow
{
    ReferenceRide? referenceRide, savedReferenceRide;

    string ReferenceRidePath => Path.Combine(Path.GetDirectoryName(settingsFile)!,
        "reference-rides", RouteFingerprint() + ".json");

    void ResetReferenceRide()
    {
        referenceRide = savedReferenceRide = null;
        RefreshSavedReferenceButton();
        RefreshReferenceRide();
    }

    void OfferSavedReferenceRide()
    {
        try
        {
            savedReferenceRide = ReferenceRide.Load(ReferenceRidePath, route[^1].D);
            RefreshSavedReferenceButton();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Status.Text = UiText.Get("Uloženou referenční jízdu se nepodařilo načíst.");
        }
    }

    void RefreshSavedReferenceButton()
    {
        SavedReferenceButton.Visibility = savedReferenceRide == null ? Visibility.Collapsed : Visibility.Visible;
        SavedReferenceButton.ToolTip = savedReferenceRide?.Name;
    }

    void UseSavedReferenceRide(object sender, RoutedEventArgs e)
    {
        if (riding || starting || ticking || savedReferenceRide == null) return;
        referenceRide = savedReferenceRide;
        savedReferenceRide = null;
        RefreshSavedReferenceButton();
        RefreshReferenceRide();
    }

    void OpenReferenceRide(object sender, RoutedEventArgs e)
    {
        if (riding || starting || ticking) return;
        if (route.Count < 2)
        {
            Status.Text = UiText.Get("Nejprve načti GPX trasu pro porovnání.");
            return;
        }
        var course = route.Select(p => new ReferenceRoutePoint(p.Lat, p.Lon, p.D)).ToArray();
        var window = new ReferenceRideWindow(historyStore, referenceRide, savedReferenceRide != null,
            source => ReferenceRide.Match(source, course))
            { Owner = this };
        if (window.ShowDialog() == true)
        {
            var selected = window.SelectedReference;
            try
            {
                if (selected == null) File.Delete(ReferenceRidePath);
                else selected.Save(ReferenceRidePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status.Text = UiText.Get("Uloženou referenční jízdu se nepodařilo změnit.");
                if (selected == null) return;
            }

            referenceRide = selected;
            savedReferenceRide = null;
            RefreshSavedReferenceButton();
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

        double elapsed = rideClock.Elapsed.TotalSeconds + referenceRide.TimeAt(rideStartDistance);
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
