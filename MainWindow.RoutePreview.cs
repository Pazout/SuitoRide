using System.Windows.Input;
using System.Windows.Shapes;

namespace SuitoRide;

public partial class MainWindow
{
    double? profilePreviewDistance;
    Line? profilePositionMarker;

    double DisplayDistance => profilePreviewDistance ?? distance;
    bool CanPreviewRoute => route.Count >= 2 && !riding && !starting && !ticking && countdown == null;

    void BeginProfilePreview(object sender, MouseButtonEventArgs e)
    {
        if (CanPreviewRoute && e.ClickCount == 2)
        {
            e.Handled = true;
            if (Profile.IsMouseCaptured) Profile.ReleaseMouseCapture();
            OpenRouteEditor();
            return;
        }
        if(!PreviewRouteAt(e.GetPosition(Profile).X))return;
        Profile.Focus();
        if(!Profile.CaptureMouse())UpdateStreetView(true);
        e.Handled = true;
    }

    void MoveProfilePreview(object sender, MouseEventArgs e)
    {
        if(Profile.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            PreviewRouteAt(e.GetPosition(Profile).X);
    }

    void FinishProfilePreview(object sender, MouseButtonEventArgs e)
    {
        if(!Profile.IsMouseCaptured)return;
        PreviewRouteAt(e.GetPosition(Profile).X);
        Profile.ReleaseMouseCapture();
        e.Handled = true;
    }

    void RefreshPreviewPanorama(object sender, MouseEventArgs e)
    {
        if(CanPreviewRoute && profilePreviewDistance.HasValue)
            UpdateStreetView(true);
    }

    void ProfilePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if(e.Key != Key.Escape || !CanPreviewRoute || !profilePreviewDistance.HasValue)return;
        ClearProfilePreview();
        UpdateView();
        UpdateStreetView(true);
        e.Handled = true;
    }

    void ClearProfilePreview()
    {
        profilePreviewDistance = null;
        if(Profile.IsMouseCaptured)Profile.ReleaseMouseCapture();
    }

    bool PreviewRouteAt(double x)
    {
        if(!CanPreviewRoute || Profile.ActualWidth <= 0)return false;
        double fraction = Math.Clamp(x / Profile.ActualWidth, 0, 1);
        profilePreviewDistance = route[^1].D * fraction;
        if(profilePositionMarker != null)
            profilePositionMarker.X1 = profilePositionMarker.X2 = fraction * Profile.ActualWidth;
        UpdateProfileReadout();
        DrawZoomProfile();
        SendDisplayedPosition();
        return true;
    }

    void UpdateProfileReadout()
    {
        double slope = SlopeAt(DisplayDistance);
        Grade.Text = $"{slope:F1} %";
        Grade.Foreground = GradeBrush(slope);
        RideInfo.Text = profilePreviewDistance is double meters
            ? UiText.Format($"Náhled trasy · {meters/1000:F2} z {route[^1].D/1000:F2} km")
            : $"Ujeto {distance/1000:F2} z {route[^1].D/1000:F2} km" + (demo ? UiText.Get(" · ukázková jízda") : "");
    }
}
