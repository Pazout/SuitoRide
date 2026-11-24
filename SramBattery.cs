using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace SuitoRide;

internal static class SramBattery
{
    public static async Task<(int Battery, BluetoothDeviceChoice Device)> ReadAsync(
        BluetoothDeviceChoice selected, bool discover, CancellationToken token)
    {
        using var device = await BluetoothDeviceConnection.OpenAsync(selected, scanFirst: discover, token);
        var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
            .AsTask(token);
        try
        {
            if (services.Status == GattCommunicationStatus.Success)
            {
                foreach (var service in services.Services)
                {
                    var levels = await service.GetCharacteristicsForUuidAsync(
                        GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached).AsTask(token);
                    if (levels.Status != GattCommunicationStatus.Success) continue;

                    foreach (var level in levels.Characteristics)
                    {
                        if (!level.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)) continue;
                        var result = await level.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(token);
                        if (result.Status != GattCommunicationStatus.Success || result.Value?.Length != 1) continue;

                        using var reader = DataReader.FromBuffer(result.Value);
                        int percent = reader.ReadByte();
                        if (percent <= 100)
                            return (percent, BluetoothDeviceConnection.Identify(selected, device));
                    }
                }
            }
        }
        finally
        {
            foreach (var service in services.Services) service.Dispose();
        }
        throw new InvalidOperationException();
    }
}
