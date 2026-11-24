using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace SuitoRide;

public partial class MainWindow
{
    readonly DispatcherTimer sramBatteryTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    BluetoothDeviceChoice? sramSelection;
    BluetoothDeviceWindow? sramDialog;
    CancellationTokenSource? sramRead;
    string? sramStatus;
    string SramSelectionPath => Path.Combine(Path.GetDirectoryName(settingsFile)!, "last-sram.json");


    string SramBondDirectory => Path.GetDirectoryName(SramSelectionPath)!;
    CancellationTokenSource? sramGearRead;
    Task? sramGearTask;
    int? sramRearGear;
    bool sramGearPaired;
    bool sramGearRejected;
    int? sramBatteryPercent;

    async Task PairSramAsync(BluetoothDeviceChoice selected, BluetoothDeviceWindow dialog)
    {
        if (sramRead != null) return;
        using var attempt = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        sramRead = attempt;
        byte[]? key = null;
        dialog.UpdateConnection(sramSelection != null, UiText.Get("Připojuji se k SRAM AXS…"), isConnecting: true);
        try
        {
            if (sramGearTask != null) await sramGearTask;
            key = await SramGear.PairAsync(selected, () => !attempt.IsCancellationRequested &&
                MessageBox.Show(dialog, UiText.Get("Podrž tlačítko AXS do pomalého blikání LED a potvrď. Mobilní aplikace SRAM AXS může potom požádat o nové připojení."),
                    "SRAM AXS", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK,
                attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            SramBondStore.Save(SramBondDirectory, selected.Name, key);
            BluetoothDeviceConnection.Save(SramSelectionPath, selected);
            if (!Equals(sramSelection, selected)) sramBatteryPercent = null;
            sramSelection = selected;
            sramGearPaired = true;
            sramGearRejected = false;
            sramStatus = UiText.Get("Převod SRAM AXS spárován.");
        }
        catch (OperationCanceledException) when (!attempt.IsCancellationRequested) { }
        catch (Exception)
        {
            if (ReferenceEquals(sramRead, attempt))
                sramStatus = UiText.Get("Převod SRAM AXS se nepodařilo spárovat.");
        }
        finally
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            if (ReferenceEquals(sramRead, attempt))
            {
                sramRead = null;
                RenderSramDisplay();
                dialog.UpdateConnection(sramSelection != null, sramStatus);
            }
        }
    }

    void StartSramGear()
    {
        if (sramGearRead != null || sramSelection is not { } selected) return;
        var lifetime = new CancellationTokenSource();
        sramGearRead = lifetime;
        sramGearTask = TrackSramGearAsync(selected, lifetime);
    }

    async Task TrackSramGearAsync(BluetoothDeviceChoice selected, CancellationTokenSource lifetime)
    {
        var token = lifetime.Token;
        byte[]? key = null;
        try
        {
            sramGearRejected = false;
            try { key = SramBondStore.Load(SramBondDirectory, selected.Name); }
            catch (CryptographicException) { sramGearRejected = true; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            sramGearPaired = key != null || sramGearRejected;
            RenderSramDisplay();


            while (!token.IsCancellationRequested)
            {
                try
                {
                    await SramGear.ObserveAsync(selected, key, gear =>
                    {
                        if (!ReferenceEquals(sramGearRead, lifetime)) return;
                        sramGearPaired |= gear.HasValue;
                        if (gear.HasValue) sramGearRejected = false;
                        sramRearGear = gear;
                        RenderSramDisplay();
                    }, token);
                    return;
                }
                catch (CryptographicException)
                {
                    if (ReferenceEquals(sramGearRead, lifetime))
                    {
                        sramRearGear = null;
                        sramGearRejected = true;
                        RenderSramDisplay();
                    }
                    return;
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    if (!ReferenceEquals(sramGearRead, lifetime)) return;
                    sramRearGear = null;
                    RenderSramDisplay();
                    await Task.Delay(TimeSpan.FromSeconds(4), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (ReferenceEquals(sramGearRead, lifetime))
            {
                sramGearPaired = true;
                sramGearRejected = true;
                RenderSramDisplay();
            }
        }
        finally
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            if (ReferenceEquals(sramGearRead, lifetime)) sramGearRead = null;
            lifetime.Dispose();
        }
    }

    void StopSramGear()
    {
        var lifetime = sramGearRead;
        sramGearRead = null;
        lifetime?.Cancel();
        sramRearGear = null;
        RenderSramDisplay();
    }

    void InitializeSram()
    {
        try { sramSelection = BluetoothDeviceConnection.Load(SramSelectionPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        sramBatteryTimer.Tick += async (_, _) =>
        {
            if (sramSelection is { } selected && sramRead == null)
                await RefreshSramBatteryAsync(selected, remember: false);
        };
        if (sramSelection is { } saved)
        {
            sramBatteryTimer.Start();
            _ = RefreshSramBatteryAsync(saved, remember: false);
            StartSramGear();
        }
    }

    void OpenSram(object sender, RoutedEventArgs e)
    {
        var dialog = new BluetoothDeviceWindow("SRAM AXS", "Vyhledat SRAM AXS",
            "Vyhledávám SRAM AXS… Probuď přehazovačku.",
            "Zařízení SRAM AXS nebylo nalezeno.", "Vyber přehazovačku SRAM ze seznamu.",
            "Vyhledávání SRAM AXS selhalo.", "Čtu baterii SRAM AXS…",
            selected: sramSelection, namePrefix: "SRAM ", pairLabel: "Spárovat převod") { Owner = this };
        sramDialog = dialog;
        StopSramGear();
        sramBatteryTimer.Stop();
        if (sramRead != null) StopSramRead();
        dialog.UpdateConnection(sramSelection != null, sramStatus);
        dialog.ConnectRequested += async selected => await RefreshSramBatteryAsync(selected, remember: true);
        dialog.PairRequested += async selected => await PairSramAsync(selected, dialog);
        dialog.DisconnectRequested += () =>
        {
            StopSramRead();
            sramSelection = null;
            sramStatus = null;
            UpdateSramDisplay(null);
        };
        try { dialog.ShowDialog(); }
        finally
        {
            sramDialog = null;
            if (sramRead != null) StopSramRead();
            if (sramSelection is { } saved && IsLoaded)
            {
                sramBatteryTimer.Start();
                _ = RefreshSramBatteryAsync(saved, remember: false);
                StartSramGear();
            }
        }
    }

    async Task RefreshSramBatteryAsync(BluetoothDeviceChoice selected, bool remember)
    {
        using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        sramRead = attempt;
        try
        {
            var (battery, device) = await SramBattery.ReadAsync(selected, discover: !remember, attempt.Token);
            if (!ReferenceEquals(sramRead, attempt)) return;
            sramSelection = device;
            sramStatus = UiText.Format($"Baterie SRAM AXS: {battery} %");
            if (remember)
            {
                try { BluetoothDeviceConnection.Save(SramSelectionPath, device); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    sramStatus += " " + UiText.Get("Volbu zařízení se nepodařilo uložit.");
                }
            }
            UpdateSramDisplay(battery);
        }
        catch (Exception)
        {
            if (!ReferenceEquals(sramRead, attempt)) return;
            sramStatus = UiText.Get("Baterie SRAM AXS není dostupná. Probuď přehazovačku.");
            // A failed selection must not keep showing an old value as current.
            UpdateSramDisplay(null);
        }
        finally
        {
            if (ReferenceEquals(sramRead, attempt)) sramRead = null;
        }
    }

    void UpdateSramDisplay(int? battery)
    {
        sramBatteryPercent = battery;
        RenderSramDisplay();
        sramDialog?.UpdateConnection(sramSelection != null, sramStatus);
    }

    void RenderSramDisplay()
    {
        string text = sramBatteryPercent is int percent ? $"SRAM · {percent} %" : "SRAM · — %";
        if (sramGearPaired)
            text += " · " + (sramRearGear is int gear ? UiText.Format($"Převod {gear}") : UiText.Get("Převod —"));
        SramBatteryStatus.Text = text;
        SramBatteryStatus.Visibility = sramSelection == null ? Visibility.Collapsed : Visibility.Visible;
        SramBatteryStatus.ToolTip = SramButton.ToolTip = sramGearRejected
            ? UiText.Get("Převod SRAM AXS vyžaduje nové spárování.")
            : sramGearPaired && sramRearGear == null ? UiText.Get("Čekám na ověřenou změnu převodu SRAM AXS.") : sramStatus;
    }

    void StopSramRead()
    {
        sramBatteryTimer.Stop();
        var attempt = sramRead;
        sramRead = null;
        attempt?.Cancel();
    }
}
