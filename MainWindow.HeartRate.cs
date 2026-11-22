using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace SuitoRide;

public partial class MainWindow
{
    static readonly Guid HeartRateServiceId=GattServiceUuids.HeartRate;
    static readonly TimeSpan HeartRateFreshness=TimeSpan.FromSeconds(5);
    readonly DispatcherTimer heartRateTimer=new() { Interval=TimeSpan.FromSeconds(1) };
    BluetoothLEDevice? heartRateDevice;
    GattDeviceService? heartRateService;
    GattCharacteristic? heartRateMeasurement;
    CancellationTokenSource? heartRateAttempt;
    BluetoothDeviceChoice? lastHeartRateDevice;
    int? heartRateBpm;
    long heartRateReceived;
    BluetoothDeviceWindow? heartRateDialog;
    Task? heartRateRestoreTask;
    bool heartRateRestoreSuperseded;
    string? heartRateConnectionActivity;

    void SetHeartRateActivity(string? activity)
    {
        heartRateConnectionActivity=activity;
        RefreshHeartRateActivity();
    }

    void RefreshHeartRateActivity() =>
        ShowBluetoothActivity(HeartRateActivity,heartRateDialog?.IsSearching==true ? "Hledám pás…" : heartRateConnectionActivity);

    string LastHeartRatePath => Path.Combine(Path.GetDirectoryName(settingsFile)!,"last-heart-rate.json");

    void InitializeHeartRate()
    {
        heartRateTimer.Tick+=(_,_)=>RefreshHeartRateDisplay();
    }

    int? CurrentHeartRate =>
        heartRateBpm.HasValue && Stopwatch.GetElapsedTime(heartRateReceived)<=HeartRateFreshness ? heartRateBpm : null;

    void RefreshHeartRateDisplay() => HeartRate.Text=CurrentHeartRate is int bpm ? $"{bpm} bpm" : "— bpm";

    async Task RestoreHeartRateAsync()
    {
        try
        {
            lastHeartRateDevice=BluetoothDeviceConnection.Load(LastHeartRatePath);
            if(lastHeartRateDevice!=null)
                await ConnectHeartRateAsync(lastHeartRateDevice,true,CancellationToken.None);
        }
        catch(Exception ex)
        {
            if(!heartRateRestoreSuperseded)
                HeartRateButton.ToolTip=UiText.Get("Hrudní pás se nepodařilo připojit.")+" "+ex.Message;
        }
        finally
        {
            if(!heartRateRestoreSuperseded)
                heartRateDialog?.UpdateConnection(heartRateDevice!=null,HeartRateButton.ToolTip as string);
        }
    }

    void CancelAutomaticHeartRateConnection()
    {
        if(heartRateRestoreTask is not { IsCompleted:false })return;
        heartRateRestoreSuperseded=true;
        SetHeartRateActivity(null);
        heartRateAttempt?.Cancel();
        HeartRateButton.ToolTip=null;
    }

    void OpenHeartRate(object sender,RoutedEventArgs e)
    {
        var dialog=new BluetoothDeviceWindow("Hrudní pás","Vyhledat hrudní pás",
            "Vyhledávám hrudní pás. Nasaď si pás, aby se aktivoval.","Žádný hrudní pás nebyl nalezen.",
            "Vyber hrudní pás ze seznamu.","Vyhledávání hrudního pásu selhalo.","Připojuji hrudní pás…",
            HeartRateServiceId,lastHeartRateDevice) { Owner=this };
        using var dialogLifetime=new CancellationTokenSource();
        heartRateDialog=dialog;
        dialog.Closed+=(_,_)=>dialogLifetime.Cancel();
        dialog.UpdateConnection(heartRateDevice!=null,HeartRateButton.ToolTip as string,heartRateAttempt!=null,
            !heartRateRestoreSuperseded&&heartRateRestoreTask is { IsCompleted:false });
        dialog.ManualScanRequested+=CancelAutomaticHeartRateConnection;
        dialog.SearchStateChanged+=RefreshHeartRateActivity;
        dialog.DisconnectRequested+=()=>{
            if(heartRateAttempt!=null){heartRateAttempt.Cancel();return;}
            DisconnectHeartRate();
            ShowHeartRateDisconnected();
        };
        dialog.ConnectRequested+=async selected=>{
            try
            {
                CancelAutomaticHeartRateConnection();
                if(heartRateRestoreTask is { IsCompleted:false } restore)await restore;
                if(dialogLifetime.IsCancellationRequested)return;
                await ConnectHeartRateAsync(selected,false,dialogLifetime.Token);
                dialog.UpdateConnection(true,HeartRateButton.ToolTip as string);
            }
            catch(Exception ex)
            {
                if(!dialogLifetime.IsCancellationRequested)
                    dialog.UpdateConnection(heartRateDevice!=null,UiText.Get("Hrudní pás se nepodařilo připojit.")+" "+ex.Message);
            }
        };
        try { dialog.ShowDialog(); }
        finally
        {
            heartRateDialog=null;
            RefreshHeartRateActivity();
        }
    }

    async Task ConnectHeartRateAsync(BluetoothDeviceChoice selected,bool automatic,CancellationToken dialogToken)
    {
        if(heartRateDevice?.BluetoothAddress==selected.Address&&
            heartRateDevice.ConnectionStatus==BluetoothConnectionStatus.Connected)return;
        using var attempt=CancellationTokenSource.CreateLinkedTokenSource(dialogToken);
        if(!automatic)attempt.CancelAfter(TimeSpan.FromSeconds(25));
        heartRateAttempt=attempt;
        var token=attempt.Token;
        BluetoothLEDevice? candidate=null;
        GattDeviceService? candidateService=null;
        try
        {
            HeartRateButton.ToolTip=UiText.Get("Připojuji hrudní pás…");
            SetHeartRateActivity(automatic ? "Hledám pás…" : "Připojuji hrudní pás…");
            candidate=await BluetoothDeviceConnection.OpenAsync(selected,automatic,token);
            token.ThrowIfCancellationRequested();
            SetHeartRateActivity("Připojuji hrudní pás…");
            if(automatic)attempt.CancelAfter(TimeSpan.FromSeconds(25));
            var services=await candidate.GetGattServicesForUuidAsync(HeartRateServiceId,BluetoothCacheMode.Uncached).AsTask(token);
            candidateService=services.Services.FirstOrDefault();
            foreach(var extra in services.Services.Skip(1))extra.Dispose();
            if(services.Status!=GattCommunicationStatus.Success||candidateService==null)
                throw new InvalidOperationException(UiText.Get("Zařízení nenabízí měření tepu."));
            var measurements=await candidateService.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.HeartRateMeasurement,BluetoothCacheMode.Uncached).AsTask(token);
            var measurement=measurements.Characteristics.FirstOrDefault(c=>(c.CharacteristicProperties&GattCharacteristicProperties.Notify)!=0);
            if(measurements.Status!=GattCommunicationStatus.Success||measurement==null||
                await measurement.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token)!=GattCommunicationStatus.Success)
                throw new InvalidOperationException(UiText.Get("Nepodařilo se zapnout příjem dat."));

            token.ThrowIfCancellationRequested();
            if(candidate.ConnectionStatus!=BluetoothConnectionStatus.Connected)
                throw new InvalidOperationException(UiText.Get("Zařízení není dostupné."));

            DisconnectHeartRate();
            heartRateDevice=candidate;
            heartRateService=candidateService;
            heartRateMeasurement=measurement;
            candidate=null;
            candidateService=null;
            heartRateDevice.ConnectionStatusChanged+=HeartRateConnectionChanged;
            heartRateMeasurement.ValueChanged+=HeartRateChanged;
            heartRateTimer.Start();
            lastHeartRateDevice=BluetoothDeviceConnection.Identify(selected,heartRateDevice);
            string status=UiText.Format($"Připojeno: {selected.Name}.");
            try { BluetoothDeviceConnection.Save(LastHeartRatePath,lastHeartRateDevice); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                status+=" "+UiText.Get("Volbu hrudního pásu se nepodařilo uložit.");
            }
            HeartRateButton.ToolTip=status;
        }
        finally
        {
            candidateService?.Dispose();
            candidate?.Dispose();
            heartRateAttempt=null;
            SetHeartRateActivity(null);
        }
    }

    void HeartRateConnectionChanged(BluetoothLEDevice sender,object args)
    {
        if(sender.ConnectionStatus!=BluetoothConnectionStatus.Disconnected)return;
        Dispatcher.BeginInvoke(()=>{
            if(!ReferenceEquals(sender,heartRateDevice)||sender.ConnectionStatus!=BluetoothConnectionStatus.Disconnected)return;
            DisconnectHeartRate();
            ShowHeartRateDisconnected();
        });
    }

    void ShowHeartRateDisconnected()
    {
        var message=UiText.Get("Hrudní pás odpojen.");
        HeartRateButton.ToolTip=message;
        heartRateDialog?.ConnectionLost(message);
    }

    void DisconnectHeartRate()
    {
        heartRateTimer.Stop();
        if(heartRateMeasurement!=null)heartRateMeasurement.ValueChanged-=HeartRateChanged;
        if(heartRateDevice!=null)heartRateDevice.ConnectionStatusChanged-=HeartRateConnectionChanged;
        heartRateMeasurement=null;
        heartRateService?.Dispose();
        heartRateService=null;
        heartRateDevice?.Dispose();
        heartRateDevice=null;
        heartRateBpm=null;
        HeartRateButton.ToolTip=null;
        RefreshHeartRateDisplay();
    }

    void HeartRateChanged(GattCharacteristic sender,GattValueChangedEventArgs args)
    {
        long received=Stopwatch.GetTimestamp();
        using var reader=DataReader.FromBuffer(args.CharacteristicValue);
        var packet=new byte[Math.Min(3,(int)reader.UnconsumedBufferLength)];
        reader.ReadBytes(packet);
        int? bpm=ReadHeartRate(packet);
        Dispatcher.BeginInvoke(()=>{
            if(!ReferenceEquals(sender,heartRateMeasurement)||received<heartRateReceived)return;
            heartRateBpm=bpm;
            heartRateReceived=received;
            RefreshHeartRateDisplay();
        });
    }

    internal static int? ReadHeartRate(ReadOnlySpan<byte> packet)
    {
        if(packet.Length<2||(packet[0]&0x06)==0x04)return null;
        bool wide=(packet[0]&1)!=0;
        if(wide&&packet.Length<3)return null;
        int bpm=wide ? packet[1]|(packet[2]<<8) : packet[1];
        return bpm>0 ? bpm : null;
    }

}
