using System.IO;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace SuitoRide;

internal sealed record BluetoothDeviceChoice(ulong Address,string Name,BluetoothAddressType AddressType=BluetoothAddressType.Unspecified,string? DeviceId=null);

internal static class BluetoothDeviceConnection
{
    static readonly TimeSpan StartupSearchTimeout=TimeSpan.FromMinutes(5);
    public static BluetoothDeviceChoice? Load(string path)
    {
        if(!File.Exists(path))return null;
        var saved=JsonSerializer.Deserialize<BluetoothDeviceChoice>(File.ReadAllText(path));
        return saved is { Address: >0 and <=0xffffffffffff } && !string.IsNullOrWhiteSpace(saved.Name) ? saved : null;
    }

    public static void Save(string path,BluetoothDeviceChoice device)
    {
        var directory=Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary=Path.Combine(directory,Path.GetRandomFileName());
        try
        {
            File.WriteAllText(temporary,JsonSerializer.Serialize(device));
            File.Move(temporary,path,overwrite:true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static BluetoothDeviceChoice Identify(BluetoothDeviceChoice selected,BluetoothLEDevice device) =>
        selected with { Address=device.BluetoothAddress, AddressType=device.BluetoothAddressType, DeviceId=device.DeviceId };

    public static async Task<BluetoothLEDevice> OpenAsync(BluetoothDeviceChoice selected,bool scanFirst,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var device=await OpenKnownDeviceAsync(selected,token);
        if(scanFirst && device?.ConnectionStatus!=BluetoothConnectionStatus.Connected)
        {
            device?.Dispose();
            var type=await FindAddressTypeAsync(selected.Address,token);
            device=await OpenKnownDeviceAsync(selected with { AddressType=type },token);
        }
        return device??throw new InvalidOperationException(UiText.Get("Zařízení není dostupné."));
    }

    static async Task<BluetoothLEDevice?> OpenKnownDeviceAsync(BluetoothDeviceChoice selected,CancellationToken token)
    {
        var device=selected.AddressType==BluetoothAddressType.Unspecified
            ? await BluetoothLEDevice.FromBluetoothAddressAsync(selected.Address).AsTask(token)
            : await BluetoothLEDevice.FromBluetoothAddressAsync(selected.Address,selected.AddressType).AsTask(token);
        if(device==null&&!string.IsNullOrWhiteSpace(selected.DeviceId))
            device=await BluetoothLEDevice.FromIdAsync(selected.DeviceId).AsTask(token);
        return device;
    }

    static async Task<BluetoothAddressType> FindAddressTypeAsync(ulong address,CancellationToken cancellation)
    {
        using var scan=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        scan.CancelAfter(StartupSearchTimeout);
        var watcher=new BluetoothLEAdvertisementWatcher { ScanningMode=BluetoothLEScanningMode.Active };
        var found=new TaskCompletionSource<BluetoothAddressType>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(BluetoothLEAdvertisementWatcher sender,BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if(args.BluetoothAddress==address)found.TrySetResult(args.BluetoothAddressType);
        }
        void Stopped(BluetoothLEAdvertisementWatcher sender,BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            if(args.Error!=BluetoothError.Success)found.TrySetException(new InvalidOperationException(args.Error.ToString()));
        }
        watcher.Received+=Received;
        watcher.Stopped+=Stopped;
        try
        {
            watcher.Start();
            return await found.Task.WaitAsync(scan.Token);
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(UiText.Get("Uložené Bluetooth zařízení se do pěti minut nenašlo."));
        }
        finally
        {
            watcher.Received-=Received;
            watcher.Stopped-=Stopped;
            if(watcher.Status==BluetoothLEAdvertisementWatcherStatus.Started)watcher.Stop();
        }
    }
}
