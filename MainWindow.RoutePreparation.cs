using System.Globalization;
using System.IO;

namespace SuitoRide;

public partial class MainWindow
{
    int routeSmoothingIterations = RouteEditWindow.DefaultSmoothingIterations;
    string RouteSmoothingPath => Path.Combine(Path.GetDirectoryName(settingsFile)!, "route-smoothing.txt");

    void RestoreRouteSmoothing()
    {
        try
        {
            if (File.Exists(RouteSmoothingPath) &&
                int.TryParse(File.ReadAllText(RouteSmoothingPath), CultureInfo.InvariantCulture, out int iterations) &&
                iterations is >= 1 and <= 61 && (iterations - 1) % 5 == 0)
                routeSmoothingIterations = iterations;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void RememberRouteSmoothing(int iterations)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RouteSmoothingPath)!);
        File.WriteAllText(RouteSmoothingPath, iterations.ToString(CultureInfo.InvariantCulture));
        routeSmoothingIterations = iterations;
    }

    // WTracks Smooth uses an in-place, distance-weighted pass and rounds to centimetres.
    // https://github.com/opoto/wtracks/blob/master/js/wtracks.js
    static void SmoothRouteElevations(List<Pt> points, int iterations)
    {
        // Rebuild from the original heights so changing intensity does not accumulate smoothing.
        var heights = points.Select(point => point.RawElevation).ToArray();
        var fractions = new double[points.Count];
        for (int i = 1; i < points.Count - 1; i++)
            fractions[i] = (points[i].D - points[i - 1].D) / (points[i + 1].D - points[i - 1].D);

        for (int pass = 0; pass < iterations; pass++)
        {
            for (int i = 1; i < heights.Length - 1; i++)
            {
                double neighborElevation = heights[i - 1] + fractions[i] * (heights[i + 1] - heights[i - 1]);
                heights[i] = Math.Floor((heights[i] + neighborElevation) * 50 + 0.5) / 100;
            }
        }

        for (int i = 0; i < points.Count; i++)
            points[i] = points[i] with { Ele = heights[i] };
    }

    static List<Pt> CompressRoute(List<Pt> points, double tolerance, double? maxDistance, double? maxSeconds)
    {
        int last = points.Count - 1;
        var keep = new bool[points.Count];
        keep[0] = keep[last] = true;

        if (maxDistance is double distanceLimit)
        {
            int anchor = 0;
            for (int i = 1; i <= last; i++)
            {
                if (points[i].D - points[anchor].D <= distanceLimit) continue;
                anchor = Math.Max(anchor + 1, i - 1);
                keep[anchor] = true;
                if (points[i].D - points[anchor].D <= distanceLimit) continue;
                anchor = i;
                keep[anchor] = true;
            }
        }

        if (maxSeconds is double timeLimit)
        {
            int? anchor = null;
            int previous = 0;
            for (int i = 0; i <= last; i++)
            {
                if (points[i].Time is not DateTimeOffset time) continue;
                if (anchor is int from && Math.Abs((time - points[from].Time!.Value).TotalSeconds) > timeLimit)
                {
                    keep[previous] = true;
                    anchor = previous == from || Math.Abs((time - points[previous].Time!.Value).TotalSeconds) > timeLimit ? i : previous;
                    keep[anchor.Value] = true;
                }
                if (anchor is null)
                {
                    anchor = i;
                    keep[i] = true;
                }
                previous = i;
            }
            if (anchor is not null) keep[previous] = true;
        }

        // Douglas–Peucker between retained anchors, using both the horizontal and vertical deviation.
        var spans = new Stack<(int First, int Last)>();
        int first = 0;
        for (int i = 1; i <= last; i++)
        {
            if (!keep[i]) continue;
            if (i - first > 1) spans.Push((first, i));
            first = i;
        }

        while (spans.TryPop(out var span))
        {
            var start = points[span.First];
            var end = points[span.Last];
            const double metersPerDegree = Math.PI * 6371000 / 180;
            double longitudeScale = metersPerDegree * Math.Cos((start.Lat + end.Lat) * Math.PI / 360);
            double east = Math.IEEERemainder(end.Lon - start.Lon, 360) * longitudeScale;
            double north = (end.Lat - start.Lat) * metersPerDegree;
            double lengthSquared = east * east + north * north;
            double largestError = tolerance * tolerance;
            int split = -1;
            for (int i = span.First + 1; i < span.Last; i++)
            {
                var point = points[i];
                double x = Math.IEEERemainder(point.Lon - start.Lon, 360) * longitudeScale;
                double y = (point.Lat - start.Lat) * metersPerDegree;
                double fraction = lengthSquared == 0 ? 0 : Math.Clamp((x * east + y * north) / lengthSquared, 0, 1);
                double dx = x - fraction * east;
                double dy = y - fraction * north;
                double dz = point.Ele - (start.Ele + fraction * (end.Ele - start.Ele));
                double error = dx * dx + dy * dy + dz * dz;
                if (error <= largestError) continue;
                largestError = error;
                split = i;
            }

            if (split < 0) continue;
            keep[split] = true;
            if (split - span.First > 1) spans.Push((span.First, split));
            if (span.Last - split > 1) spans.Push((split, span.Last));
        }

        var compressed = new List<Pt> { points[0] };
        for (int i = 1; i <= last; i++)
        {
            if (!keep[i]) continue;
            var previous = compressed[^1];
            var point = points[i];
            double step = Dist(previous.Lat, previous.Lon, point.Lat, point.Lon);
            if (step < 0.000001) return points;
            compressed.Add(point with { D = previous.D + step });
        }
        return compressed[^1].D >= 10 ? compressed : points;
    }

    DateTimeOffset? RouteTimeAt(double distance)
    {
        int index = route.FindIndex(point => point.D >= distance);
        if (index <= 0) return route[0].Time;
        var right = route[index];
        if (right.D == distance) return right.Time;
        var left = route[index - 1];
        if (left.Time is not DateTimeOffset start || right.Time is not DateTimeOffset end) return null;
        return start.AddTicks((long)((end - start).Ticks * ((distance - left.D) / (right.D - left.D))));
    }
}
