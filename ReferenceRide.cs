using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace SuitoRide;

internal sealed record ReferenceSource(string Name, IReadOnlyList<ReferencePoint> Points)
{
    public static ReferenceSource FromHistory(RideRecord ride)
    {
        var points = new List<ReferencePoint> { new(0, ride.StartPosition) };
        if (ride.Samples != null)
            points.AddRange(ride.Samples.Select(sample => new ReferencePoint(sample.ElapsedSeconds, sample.Position)));
        points.Add(new(ride.DurationSeconds, ride.EndPosition));
        return new(ride.RouteName, points);
    }

    public static ReferenceSource FromTcx(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = 100_000_000
        });
        var document = XDocument.Load(reader);
        var root = document.Root;
        if (root?.Name.LocalName != "TrainingCenterDatabase") throw InvalidRecording();
        var ns = root.Name.Namespace;
        var activities = root.Element(ns + "Activities")?.Elements(ns + "Activity").Take(2).ToArray();
        if (activities?.Length != 1) throw InvalidRecording();

        DateTimeOffset? start = null, previous = null;
        var points = new List<ReferencePoint>();
        foreach (var point in activities[0].Elements(ns + "Lap")
                     .Elements(ns + "Track").Elements(ns + "Trackpoint"))
        {
            if (!DateTimeOffset.TryParse((string?)point.Element(ns + "Time"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var time))
                continue;
            if (previous is { } earlier && time < earlier) throw InvalidRecording();
            start ??= time;
            previous = time;
            var position = point.Element(ns + "Position");
            RidePosition? coordinates = null;
            if (double.TryParse((string?)position?.Element(ns + "LatitudeDegrees"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                double.TryParse((string?)position?.Element(ns + "LongitudeDegrees"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                coordinates = new(lat, lon, 0);
            points.Add(new((time - start.Value).TotalSeconds, coordinates));
        }
        return new(Path.GetFileNameWithoutExtension(path), points);
    }

    internal static InvalidDataException InvalidRecording() =>
        new(UiText.Get("Referenční jízda potřebuje čas a GPS body od startu až do cíle."));
}

internal readonly record struct ReferencePoint(double Seconds, RidePosition? Position);
internal readonly record struct ReferenceRoutePoint(double Latitude, double Longitude, double Meters);

internal sealed class ReferenceRide
{
    readonly record struct Sample(double Seconds, double Meters);
    readonly Sample[] samples;
    public string Name { get; }

    ReferenceRide(string name, Sample[] samples)
    {
        Name = name;
        this.samples = samples;
    }

    public static ReferenceRide Match(ReferenceSource source, IReadOnlyList<ReferenceRoutePoint> route)
    {
        if (route.Count < 2) throw Mismatch();
        double length = route[^1].Meters;
        var recorded = source.Points;
        var positioned = new List<(ReferencePoint Point, double RecordedMeters)>();
        double traveled = 0, previousTime = -1;
        RidePosition? last = null;
        foreach (var point in recorded)
        {
            if (!double.IsFinite(point.Seconds) || point.Seconds < previousTime || point.Seconds < 0)
                throw ReferenceSource.InvalidRecording();
            previousTime = point.Seconds;
            if (point.Position is not { Latitude: >= -90 and <= 90, Longitude: >= -180 and <= 180 } coordinates)
                continue;
            if (last != null)
                traveled += Distance(last.Latitude, last.Longitude, coordinates.Latitude, coordinates.Longitude);
            positioned.Add((point, traveled));
            last = coordinates;
        }

        if (positioned.Count < 2 || traveled <= 0 || previousTime <= 0)
            throw ReferenceSource.InvalidRecording();

        var start = positioned[0].Point.Position!;
        var finish = positioned[^1].Point.Position!;
        double endpointTolerance = Math.Clamp(length * 0.1, 2, 45);
        if (Distance(start.Latitude, start.Longitude, route[0].Latitude, route[0].Longitude) > endpointTolerance ||
            Distance(finish.Latitude, finish.Longitude, route[^1].Latitude, route[^1].Longitude) > endpointTolerance)
            throw Mismatch();

        var aligned = new List<Sample> { new(0, 0) };
        double previousMeters = 0;
        for (int i = 0; i < positioned.Count; i++)
        {
            var (point, recordedMeters) = positioned[i];
            bool atEnd = i == positioned.Count - 1;
            double meters = i == 0 ? 0 : atEnd ? length :
                Project(point.Position!, route, recordedMeters / traveled * length, previousMeters);

            if (meters - previousMeters > 500 || double.IsNaN(meters)) throw Mismatch();
            if (point.Seconds > aligned[^1].Seconds)
                aligned.Add(new(point.Seconds, meters));
            else if (Math.Abs(meters - aligned[^1].Meters) > 5)
                throw ReferenceSource.InvalidRecording();
            previousMeters = meters;
        }
        if (previousTime > aligned[^1].Seconds) aligned.Add(new(previousTime, length));
        if (aligned.Count < 2 || aligned[^1].Meters != length) throw ReferenceSource.InvalidRecording();
        return new(source.Name, aligned.ToArray());
    }

    static InvalidDataException Mismatch() =>
        new(UiText.Get("Jízda neodpovídá načtené trase, směru nebo úseku GPX."));

    static double Project(RidePosition point, IReadOnlyList<ReferenceRoutePoint> route, double expected, double previous)
    {
        double window = Math.Max(200, route[^1].Meters * 0.08);
        double min = Math.Max(previous - 40, expected - window);
        double max = Math.Min(previous + 500, expected + window);
        if (max < min) return double.NaN;

        int lo = 1, hi = route.Count - 1;
        while (lo < hi)
        {
            int middle = (lo + hi) / 2;
            if (route[middle].Meters < min) lo = middle + 1;
            else hi = middle;
        }

        double best = double.NaN, bestScore = double.PositiveInfinity;
        for (int i = lo; i < route.Count && route[i - 1].Meters <= max; i++)
        {
            var a = route[i - 1];
            var b = route[i];
            var (ax, ay) = Offset(point.Latitude, point.Longitude, a.Latitude, a.Longitude);
            var (bx, by) = Offset(point.Latitude, point.Longitude, b.Latitude, b.Longitude);
            double dx = bx - ax, dy = by - ay;
            double norm = dx * dx + dy * dy;
            if (norm <= 0) continue;
            double part = Math.Clamp(-(ax * dx + ay * dy) / norm, 0, 1);
            double meters = a.Meters + (b.Meters - a.Meters) * part;
            if (meters < min || meters > max) continue;
            double lateral = Math.Sqrt(Math.Pow(ax + part * dx, 2) + Math.Pow(ay + part * dy, 2));
            if (lateral > 45) continue;
            double score = lateral + 0.12 * Math.Abs(meters - expected);
            if (score >= bestScore) continue;
            bestScore = score;
            best = Math.Max(previous, meters);
        }
        return best;
    }

    static (double X, double Y) Offset(double lat, double lon, double otherLat, double otherLon)
    {
        const double radians = Math.PI / 180;
        const double radius = 6_371_000;
        return (radius * radians * Math.IEEERemainder(otherLon - lon, 360) * Math.Cos(lat * radians),
            radius * radians * (otherLat - lat));
    }

    static double Distance(double lat, double lon, double otherLat, double otherLon)
    {
        var (x, y) = Offset(lat, lon, otherLat, otherLon);
        return Math.Sqrt(x * x + y * y);
    }

    public double TimeAt(double meters) => Interpolate(meters, sample => sample.Meters, sample => sample.Seconds);
    public double DistanceAt(double seconds) => Interpolate(seconds, sample => sample.Seconds, sample => sample.Meters);

    double Interpolate(double value, Func<Sample, double> input, Func<Sample, double> output)
    {
        if (value <= input(samples[0])) return output(samples[0]);
        if (value > input(samples[^1])) return output(samples[^1]);
        int lo = 1, hi = samples.Length - 1;
        while (lo < hi)
        {
            int middle = (lo + hi) / 2;
            if (input(samples[middle]) < value) lo = middle + 1;
            else hi = middle;
        }
        var before = samples[lo - 1];
        var after = samples[lo];
        double part = (value - input(before)) / (input(after) - input(before));
        return output(before) + (output(after) - output(before)) * part;
    }
}
