using System.Diagnostics;
using System.Windows;
using Windows.Devices.Bluetooth;

namespace SuitoRide;

public partial class MainWindow
{
    static readonly TimeSpan StartTelemetryTimeout = TimeSpan.FromSeconds(3);
    CancellationTokenSource? countdown;
    RideStartCountdown? rideStartCountdown;
    bool starting;

    bool TrainerIsConnected => control != null && device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    async void StartRide(object sender, RoutedEventArgs e)
    {
        if (riding || ticking || starting) return;
        if (route.Count < 2)
        {
            ShowRideStartIssue(UiText.Get("Nejprve načtěte GPX."));
            return;
        }
        if (pendingRide != null)
        {
            ShowRideStartIssue(UiText.Get("Nejprve ulož čekající jízdu."));
            return;
        }
        if (!TrainerIsConnected && trainerConnection == null)
        {
            Status.Text = UiText.Get("Nejprve připojte trenažér.");
            OpenTrainer(sender, e);
            if (!TrainerIsConnected && trainerConnection == null) return;
        }

        using var cancellation = new CancellationTokenSource();
        countdown = cancellation;
        var sequence = new RideStartCountdown();
        rideStartCountdown = sequence;
        starting = true;
        StartButton.IsEnabled = DemoButton.IsEnabled = SetupPanel.IsEnabled = false;
        StopButton.Visibility = Visibility.Visible;
        CountdownWindow? overlay = null;
        string? failure = null;
        try
        {
            overlay = new CountdownWindow(CancelRideStart);
            if (IsVisible)
            {
                overlay.Owner = this;
                if (GetMonitorBounds(this) is Rect bounds) overlay.PlaceOnMonitor(bounds);
            }
            overlay.Show();
            if (IsVisible) PlaceOnOwnerMonitor(overlay, this);

            var clock = Stopwatch.StartNew();
            int? previous = null;
            bool interrupted = false;
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (trainerConnection != null)
                {
                    overlay.ShowConnecting();
                    await Task.Delay(100, cancellation.Token);
                    continue;
                }
                if (!TrainerIsConnected)
                    throw new InvalidOperationException(UiText.Get("Trenažér není připojen."));

                var now = DateTime.UtcNow;
                double? liveSpeed = now - lastData <= StartTelemetryTimeout ? trainerSpeed : null;
                double? liveCadence = now - cadenceReceived <= StartTelemetryTimeout ? currentCadence : null;
                int? remaining = sequence.Update(clock.Elapsed, liveSpeed, liveCadence);
                if (remaining == 0) break;

                if (remaining != previous)
                {
                    interrupted = !remaining.HasValue;
                    if (remaining is >= 1 and <= 3) PlayCountdownSound(false);
                    previous = remaining;
                }
                overlay.UpdateStart(remaining, liveSpeed, liveCadence, interrupted);
                await Task.Delay(100, cancellation.Token);
            }

            overlay.ShowStarting();
            await Command([7]);
            if (cancellation.IsCancellationRequested)
            {
                if (TrainerIsConnected) await Command([8, 1]);
                return;
            }
            if (!TrainerIsConnected)
                throw new InvalidOperationException(UiText.Get("Trenažér není připojen."));

            demo = false;
            BeginRide();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
                failure = UiText.Get("Jízdu nelze spustit: ") + ex.Message;
        }
        finally
        {
            overlay?.Dismiss();
            countdown = null;
            rideStartCountdown = null;
            starting = false;
            StartButton.IsEnabled = DemoButton.IsEnabled = SetupPanel.IsEnabled = true;
            if (!riding) StopButton.Visibility = Visibility.Collapsed;
        }
        if (failure != null) ShowRideStartIssue(failure);
    }

    void ShowRideStartIssue(string message)
    {
        Status.Text = message;
        if (IsVisible)
            MessageBox.Show(this, message, UiText.Get("Příprava na jízdu"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void CancelRideStart()
    {
        if (countdown is not { IsCancellationRequested: false }) return;
        Status.Text = UiText.Get("Příprava na jízdu zrušena.");
        countdown.Cancel();
    }
}
