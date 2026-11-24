using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SuitoRide;

public partial class MainWindow
{
    sealed record GradeBand(double UpperLimit, Brush Color, string Label);

    static readonly GradeBand[] GradeBands =
    [
        new(-3, Brushes.DodgerBlue, "< −3 %"),
        new(0, Brushes.DeepSkyBlue, "−3…<0 %"),
        new(3, Brushes.MediumAquamarine, "0…<3 %"),
        new(6, Brushes.Gold, "3…<6 %"),
        new(10, Brushes.DarkOrange, "6…<10 %"),
        new(double.PositiveInfinity, Brushes.OrangeRed, "≥ 10 %")
    ];

    static readonly Brush ProfileSpeedBrush = new SolidColorBrush(Color.FromRgb(196, 181, 253));
    static readonly Brush ProfileHeartRateBrush = new SolidColorBrush(Color.FromRgb(253, 164, 175));
    static readonly DoubleCollection ProfileSpeedDashes = new([4, 2]);
    static readonly DoubleCollection ProfileHeartRateDashes = new([1, 2]);

    static readonly Brush ProfileCadenceBrush = new SolidColorBrush(Color.FromRgb(103, 232, 249));
    static readonly Brush ProfilePowerBrush = new SolidColorBrush(Color.FromRgb(190, 242, 100));
    static readonly DoubleCollection ProfileCadenceDashes = new([4, 1, 1, 1]);
    static readonly DoubleCollection ProfilePowerDashes = new([7, 3]);

    const double DefaultSpeedScale = 60;
    const int DefaultHeartRateScale = 220;
    double profileSpeedScale = DefaultSpeedScale;
    double profileHeartRateScale = DefaultHeartRateScale;
    const double DefaultCadenceScale = 120;
    const double DefaultPowerScale = 400;
    double profileCadenceScale = DefaultCadenceScale;
    double profilePowerScale = DefaultPowerScale;
    readonly List<ProfileSample> profileSamples = [];
    readonly record struct ProfileSample(double Distance, double? SpeedKmh, int? HeartRateBpm, double? CadenceRpm, double? PowerWatts);

    static Brush GradeBrush(double grade)
    {
        foreach (var band in GradeBands)
            if (grade < band.UpperLimit) return band.Color;
        return GradeBands[^1].Color;
    }

    void InitializeProfileLegend()
    {
        GradeLegend.ItemsSource = GradeBands;
        SpeedLegendLine.Stroke = ProfileSpeedBrush;
        SpeedLegendLine.StrokeDashArray = ProfileSpeedDashes;
        HeartRateLegendLine.Stroke = ProfileHeartRateBrush;
        HeartRateLegendLine.StrokeDashArray = ProfileHeartRateDashes;
        CadenceLegendLine.Stroke = ProfileCadenceBrush;
        CadenceLegendLine.StrokeDashArray = ProfileCadenceDashes;
        PowerLegendLine.Stroke = ProfilePowerBrush;
        PowerLegendLine.StrokeDashArray = ProfilePowerDashes;
        UpdateProfileLegendScales();
        UpdateZoomProfileLabel();
    }

    void UpdateProfileLegendScales()
    {
        SpeedLegendLabel.Text = UiText.Format($"Rychlost 0–{profileSpeedScale:0} km/h");
        HeartRateLegendLabel.Text = UiText.Format($"Tep 0–{profileHeartRateScale:0} bpm");
        CadenceLegendLabel.Text = UiText.Format($"Kadence 0–{profileCadenceScale:0} ot/min");
        PowerLegendLabel.Text = UiText.Format($"Výkon 0–{profilePowerScale:0} W");
    }

    void ResetProfileTelemetry()
    {
        profileSamples.Clear();
        profileSpeedScale = DefaultSpeedScale;
        profileHeartRateScale = DefaultHeartRateScale;
        profileCadenceScale = DefaultCadenceScale;
        profilePowerScale = DefaultPowerScale;
        UpdateProfileLegendScales();
    }

    void AddProfileTelemetry(double meters, double? speedKmh, int? heartRateBpm, double? cadenceRpm, double? powerWatts)
    {
        profileSamples.Add(new(meters, speedKmh, heartRateBpm, cadenceRpm, powerWatts));
        var previous = (profileSpeedScale, profileHeartRateScale, profileCadenceScale, profilePowerScale);
        profileSpeedScale = ExpandScale(profileSpeedScale, speedKmh, 10);
        profileHeartRateScale = ExpandScale(profileHeartRateScale, heartRateBpm, 20);
        profileCadenceScale = ExpandScale(profileCadenceScale, cadenceRpm, 20);
        profilePowerScale = ExpandScale(profilePowerScale, powerWatts, 100);
        if (previous != (profileSpeedScale, profileHeartRateScale, profileCadenceScale, profilePowerScale))
            UpdateProfileLegendScales();
    }

    static double ExpandScale(double scale, double? sample, double step) =>
        sample is double value && value > scale ? Math.Ceiling(value / step) * step : scale;

    void DrawProfileTelemetry(Canvas canvas, Func<double, double> x, double height)
    {
        DrawProfileSeries(canvas, x, height, sample => sample.SpeedKmh, profileSpeedScale, ProfileSpeedBrush, ProfileSpeedDashes);
        DrawProfileSeries(canvas, x, height, sample => sample.HeartRateBpm, profileHeartRateScale, ProfileHeartRateBrush, ProfileHeartRateDashes);
        DrawProfileSeries(canvas, x, height, sample => sample.CadenceRpm, profileCadenceScale, ProfileCadenceBrush, ProfileCadenceDashes);
        DrawProfileSeries(canvas, x, height, sample => sample.PowerWatts, profilePowerScale, ProfilePowerBrush, ProfilePowerDashes);
    }

    void DrawProfileSeries(Canvas canvas, Func<double, double> x, double height,
        Func<ProfileSample, double?> valueOf, double scale, Brush brush, DoubleCollection dashes)
    {
        if (profileSamples.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            bool connected = false;
            foreach (var sample in profileSamples)
            {
                if (valueOf(sample) is not double value)
                {
                    connected = false;
                    continue;
                }
                var point = new Point(x(sample.Distance), 6 + height * (1 - value / scale));
                if (connected) drawing.LineTo(point, isStroked: true, isSmoothJoin: false);
                else drawing.BeginFigure(point, isFilled: false, isClosed: false);
                connected = true;
            }
        }
        geometry.Freeze();
        canvas.Children.Add(new Path
        {
            Data = geometry, Stroke = brush, StrokeDashArray = dashes, StrokeThickness = 2, IsHitTestVisible = false
        });
    }
}
