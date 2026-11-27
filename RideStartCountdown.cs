namespace SuitoRide;

internal sealed class RideStartCountdown
{
    public const int SpeedThresholdKmh = 15;
    const int DurationSeconds = 5;
    TimeSpan? startedAt;
    bool stoppedBetweenUpdates;

    public void ObserveStop()
    {
        if (!startedAt.HasValue) return;
        startedAt = null;
        stoppedBetweenUpdates = true;
    }

    public int? Update(TimeSpan elapsed, double? speed, double? cadence)
    {
        if (stoppedBetweenUpdates || !(speed > 0) || !(cadence > 0))
        {
            stoppedBetweenUpdates = false;
            startedAt = null;
            return null;
        }

        if (!startedAt.HasValue && speed > SpeedThresholdKmh)
            startedAt = elapsed;

        return startedAt is TimeSpan start
            ? (int)Math.Max(0, Math.Ceiling(DurationSeconds - (elapsed - start).TotalSeconds))
            : null;
    }
}
