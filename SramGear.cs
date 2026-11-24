using System.IO;
using System.Security.Cryptography;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace SuitoRide;

internal static class SramGear
{
    static readonly Guid ForceStateService = SramUuid("0005");
    static readonly Guid ForceLiveGear = SramUuid("0009");
    static readonly Guid GearService = SramUuid("0020");
    static readonly Guid GearLevel = SramUuid("0021");
    static readonly Guid BondService = SramUuid("ee51");
    static readonly Guid BondExchange = SramUuid("ee52");

    internal static async Task<byte[]> PairAsync(BluetoothDeviceChoice selected, Func<bool> confirm, CancellationToken token)
    {
        using var device = await BluetoothDeviceConnection.OpenAsync(selected, scanFirst: false, token);
        using var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token);
        session.MaintainConnection = true;
        using var service = await OpenServiceAsync(device, BondService, token);
        var exchange = await GetCharacteristicAsync(service, BondExchange, token);
        var responses = Channel.CreateBounded<byte[]>(8);
        void HandleValue(GattCharacteristic sender, GattValueChangedEventArgs e)
        {
            if (e.CharacteristicValue.Length is > 0 and <= 64)
                responses.Writer.TryWrite(Copy(e.CharacteristicValue));
        }
        exchange.ValueChanged += HandleValue;
        byte[] secret = RandomNumberGenerator.GetBytes(16);
        try
        {
            var status = await exchange.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token);
            if (status != GattCommunicationStatus.Success) throw new IOException();
            if (!confirm()) throw new OperationCanceledException();
            token.ThrowIfCancellationRequested();

            // Force AXS publishes the peer key in response to init. The authenticated
            // key envelope arrives only after sending the laptop public key.
            await WriteAsync(exchange, SramGearProtocol.Init, token);
            byte[] peer = await responses.Reader.ReadAsync(token);
            if (peer.Length != 16) throw new InvalidDataException();
            await WriteAsync(exchange, SramGearProtocol.PublicKey(secret), token);
            byte[] envelope = new byte[48];
            for (int filled = 0; filled < envelope.Length;)
            {
                byte[] fragment = await responses.Reader.ReadAsync(token);
                if (fragment.Length > envelope.Length - filled) throw new InvalidDataException();
                fragment.CopyTo(envelope, filled);
                filled += fragment.Length;
            }

            byte[] key = SramGearProtocol.ReadBondKey(secret, peer, envelope);
            try
            {
                await WriteAsync(exchange, [0x73], token);
                return key;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }
        finally
        {
            exchange.ValueChanged -= HandleValue;
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    internal static async Task ObserveAsync(BluetoothDeviceChoice selected, byte[]? key,
        Action<int?> report, CancellationToken token)
    {
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
        connect.CancelAfter(TimeSpan.FromSeconds(15));
        var start = connect.Token;
        using var device = await BluetoothDeviceConnection.OpenAsync(selected, scanFirst: false, start);
        using var stateService = await FindServiceAsync(device, ForceStateService, start);
        if (stateService != null)
        {
            var liveGear = await FindCharacteristicAsync(stateService, ForceLiveGear, start);
            if (liveGear != null && liveGear.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read) &&
                liveGear.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                // This Force AXS firmware announces a change on 0009. The value
                // itself is the unencrypted drivetrain status returned by a read.
                await ObserveCharacteristicAsync(liveGear, frame => SramGearProtocol.ReadRearGear(frame), report, token);
                return;
            }
        }

        if (key == null) return;
        using var service = await OpenServiceAsync(device, GearService, start);
        var gear = await GetCharacteristicAsync(service, GearLevel, start);
        int? previous = null;
        bool live = false;
        await ObserveCharacteristicAsync(gear, frame => SramGearProtocol.ReadAuthenticatedRearGear(key, frame), value =>
        {
            // The older encrypted channel can expose a fixed state. Report it
            // only if an actual change is observed.
            if (value is int position)
            {
                live |= previous is int old && old != position;
                previous = position;
            }
            report(live ? value : null);
        }, token);
    }

    static async Task ObserveCharacteristicAsync(GattCharacteristic gear, Func<byte[], int?> decode,
        Action<int?> report, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
            read.CancelAfter(TimeSpan.FromSeconds(4));
            var result = await gear.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(read.Token);
            if (result.Status != GattCommunicationStatus.Success || result.Value == null) throw new IOException();
            token.ThrowIfCancellationRequested();
            report(decode(Copy(result.Value)));
            await Task.Delay(500, token);
        }
    }

    static async Task<GattDeviceService> OpenServiceAsync(BluetoothLEDevice device, Guid service, CancellationToken token) =>
        await FindServiceAsync(device, service, token) ?? throw new IOException();

    static async Task<GattDeviceService?> FindServiceAsync(BluetoothLEDevice device, Guid service, CancellationToken token)
    {
        var result = await device.GetGattServicesForUuidAsync(service, BluetoothCacheMode.Uncached).AsTask(token);
        var first = result.Services.FirstOrDefault();
        foreach (var extra in result.Services.Skip(1)) extra.Dispose();
        if (result.Status == GattCommunicationStatus.Success) return first;
        first?.Dispose();
        if (result.Status == GattCommunicationStatus.ProtocolError && result.ProtocolError == GattProtocolError.AttributeNotFound)
            return null;
        throw new IOException();
    }

    static async Task<GattCharacteristic> GetCharacteristicAsync(GattDeviceService service, Guid id, CancellationToken token) =>
        await FindCharacteristicAsync(service, id, token) ?? throw new IOException();

    static async Task<GattCharacteristic?> FindCharacteristicAsync(GattDeviceService service, Guid id, CancellationToken token)
    {
        var result = await service.GetCharacteristicsForUuidAsync(id, BluetoothCacheMode.Uncached).AsTask(token);
        if (result.Status == GattCommunicationStatus.Success) return result.Characteristics.FirstOrDefault();
        if (result.Status == GattCommunicationStatus.ProtocolError && result.ProtocolError == GattProtocolError.AttributeNotFound)
            return null;
        throw new IOException();
    }

    static async Task WriteAsync(GattCharacteristic destination, byte[] bytes, CancellationToken token)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        if (await destination.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask(token)
            != GattCommunicationStatus.Success) throw new IOException();
    }

    static byte[] Copy(IBuffer buffer)
    {
        if (buffer.Length > 512) throw new InvalidDataException();
        using var reader = DataReader.FromBuffer(buffer);
        byte[] bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    static Guid SramUuid(string id) => new($"d905{id}-90aa-4c7c-b036-1e01fb8eb7ee");
}
