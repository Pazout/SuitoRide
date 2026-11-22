using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SuitoRide;

public sealed record RideSample(double ElapsedSeconds, double DistanceMeters, double? SpeedKmh, double? CadenceRpm, double? PowerWatts);

public sealed record RideRecord(
    Guid Id, DateTimeOffset StartedAt, string RouteName, string Result,
    double DurationSeconds, double DistanceMeters, double? AveragePowerWatts, IReadOnlyList<RideSample>? Samples = null, string? RoutePath = null)
{
    public string DateDisplay => StartedAt.ToLocalTime().ToString("g");
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResultDisplay => UiText.Get(Result);
    public string DurationDisplay => $"{(long)(DurationSeconds/3600):D2}:{(int)(DurationSeconds/60)%60:D2}:{(int)DurationSeconds%60:D2}";
    public string DistanceDisplay => $"{DistanceMeters / 1000:F2} km";
    public string AverageSpeedDisplay => DurationSeconds > 0 ? $"{DistanceMeters / DurationSeconds * 3.6:F1} km/h" : "—";
    public string PowerDisplay => AveragePowerWatts is double p ? $"{p:F0} W" : "—";
}

internal sealed class RideSession
{
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly DateTimeOffset startedAt = DateTimeOffset.Now;
    readonly string routeName;
    readonly string? routePath;
    readonly double initialDistance;
    double powerSum, powerSeconds;
    readonly List<RideSample> samples=[];
    public RideSession(string name, double distance, string? path = null) { routeName=name; initialDistance=distance; routePath=path; }
    public void AddPower(double watts, double seconds)
    {
        if (watts >= 0 && double.IsFinite(watts) && seconds > 0) { powerSum += watts * seconds; powerSeconds += seconds; }
    }
    public void AddSample(double distance,double? speed,double? cadence,double? power)
    {
        samples.Add(new(clock.Elapsed.TotalSeconds,Math.Max(0,distance-initialDistance),speed,cadence,power));
    }
    public RideRecord Finish(double distance, string result)
    {
        clock.Stop();
        return new(Guid.NewGuid(), startedAt, routeName, result, clock.Elapsed.TotalSeconds,
            Math.Max(0,distance-initialDistance), powerSeconds > 0 ? powerSum / powerSeconds : null, samples.ToArray(),routePath);
    }
}

internal sealed class RideHistoryStore
{
    readonly string folder;
    public RideHistoryStore(string? directory = null) => folder = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuitoRide", "rides");
    public void Save(RideRecord ride)
    {
        Directory.CreateDirectory(folder);
        string target=Path.Combine(folder,ride.Id.ToString("N")+".json");
        if(File.Exists(target)) return;
        string temp=Path.Combine(folder,ride.Id.ToString("N")+".tmp");
        try
        {
            File.WriteAllText(temp,JsonSerializer.Serialize(ride),new System.Text.UTF8Encoding(false));
            File.Move(temp,target,false);
        }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    public (List<RideRecord> Rides, int Invalid) Load()
    {
        List<RideRecord> rides=[]; int invalid=0;
        if(!Directory.Exists(folder))return (rides,invalid);
        foreach(var path in Directory.EnumerateFiles(folder,"*.json"))
        {
            try
            {
                var ride=JsonSerializer.Deserialize<RideRecord>(File.ReadAllText(path));
                if(ride==null || ride.Id==Guid.Empty || string.IsNullOrWhiteSpace(ride.RouteName) ||
                   !double.IsFinite(ride.DurationSeconds) || ride.DurationSeconds<0 ||
                   !double.IsFinite(ride.DistanceMeters) || ride.DistanceMeters<0)
                    throw new InvalidDataException();
                rides.Add(ride);
            }
            catch(Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { invalid++; }
        }
        return (rides.OrderByDescending(r=>r.StartedAt).ToList(),invalid);
    }
}
