namespace SuitoRide;

internal sealed class ElevationProfile
{
    const double SmoothingRadiusMeters = 50;
    readonly double[] distances;
    readonly double[] elevations;
    readonly double[] integratedElevations;

    public ElevationProfile(IEnumerable<(double Distance, double Elevation)> points)
    {
        var samples = points.ToArray();
        distances = new double[samples.Length];
        elevations = new double[samples.Length];
        integratedElevations = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            (distances[i], elevations[i]) = samples[i];
            if (i > 0)
                integratedElevations[i] = integratedElevations[i - 1] +
                    (distances[i] - distances[i - 1]) * (elevations[i] + elevations[i - 1]) / 2;
        }
    }

    public double At(double distance)
    {
        double end = distances[^1];
        if (distance <= 0) return elevations[0];
        if (distance >= end) return elevations[^1];

        // Average by distance over a symmetric window, including near the ends.
        // Integrating the line between points avoids favouring densely sampled sections.
        double radius = Math.Min(SmoothingRadiusMeters, Math.Min(distance, end - distance));
        return (IntegralAt(distance + radius) - IntegralAt(distance - radius)) / (2 * radius);
    }

    public (double Distance, double Elevation)[] Sample(double start, double end, double width)
    {
        int segments = (int)Math.Clamp(Math.Ceiling(width / 2), 2, 1000);
        var points = new (double Distance, double Elevation)[segments + 1];
        for (int i = 0; i < segments; i++)
        {
            double distance = start + (end - start) * i / segments;
            points[i] = (distance, At(distance));
        }
        points[^1] = (end, At(end));
        return points;
    }

    double IntegralAt(double distance)
    {
        int index = Array.BinarySearch(distances, distance);
        if (index >= 0) return integratedElevations[index];
        int right = ~index;
        int left = right - 1;
        double span = distance - distances[left];
        double fraction = span / (distances[right] - distances[left]);
        double height = elevations[left] + (elevations[right] - elevations[left]) * fraction;
        return integratedElevations[left] + span * (elevations[left] + height) / 2;
    }
}
