using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace SuitoRide;

internal sealed class BluetoothDeviceWindow : SuitoDialog
{
    readonly ObservableCollection<BluetoothDeviceChoice> devices=[];
    readonly Dictionary<ulong,string> names=[];
    readonly ListBox list;
    readonly Button scan,connect,disconnect;
    readonly TextBlock status=new() { TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,8,0,0) };
    readonly DispatcherTimer scanTimer=new() { Interval=TimeSpan.FromSeconds(12) };
    BluetoothLEAdvertisementWatcher? watcher;
    readonly Guid? serviceFilter;
    readonly string scanningMessage, emptyMessage, selectionMessage, scanErrorMessage, connectingMessage;
    bool connected, connecting, automaticConnection;
    public event Action<BluetoothDeviceChoice>? ConnectRequested;
    public event Action? DisconnectRequested;
    public event Action? ManualScanRequested;
    public event Action? SearchStateChanged;
    public bool IsSearching => watcher!=null;

    public BluetoothDeviceWindow(string title,string scanLabel,string scanningMessage,string emptyMessage,
        string selectionMessage,string scanErrorMessage,string connectingMessage,Guid? serviceFilter=null,
        BluetoothDeviceChoice? selected=null)
    {
        Title=UiText.Get(title);
        this.serviceFilter=serviceFilter;
        this.scanningMessage=scanningMessage;
        this.emptyMessage=emptyMessage;
        this.selectionMessage=selectionMessage;
        this.scanErrorMessage=scanErrorMessage;
        this.connectingMessage=connectingMessage;
        Width=450;
        SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel { Margin=new Thickness(16) };
        list=new ListBox { Height=130, ItemsSource=devices, DisplayMemberPath=nameof(BluetoothDeviceChoice.Name) };
        list.SelectionChanged+=(_,_)=>RefreshButtons();
        panel.Children.Add(list);
        var actions=new WrapPanel { Margin=new Thickness(0,8,0,0) };
        scan=AddButton(ButtonIcons.Search,scanLabel,()=>{
            ManualScanRequested?.Invoke();
            if(automaticConnection)
            {
                connecting=false;
                automaticConnection=false;
                RefreshButtons();
            }
            StartScan();
        });
        connect=AddButton(ButtonIcons.Connect,"Připojit vybraný",()=>{
            if(list.SelectedItem is not BluetoothDeviceChoice selected)return;
            StopScan();
            connecting=true;
            automaticConnection=false;
            status.Text=UiText.Get(this.connectingMessage);
            RefreshButtons();
            ConnectRequested?.Invoke(selected);
        });
        disconnect=AddButton(ButtonIcons.Disconnect,"Odpojit",()=>DisconnectRequested?.Invoke());
        panel.Children.Add(actions);
        panel.Children.Add(status);
        Content=panel;
        scanTimer.Tick+=(_,_)=>{
            StopScan();
            status.Text=UiText.Get(devices.Count==0 ? this.emptyMessage : this.selectionMessage);
        };
        if(selected!=null)
        {
            devices.Add(selected);
            list.SelectedItem=selected;
        }
        Loaded+=(_,_)=>{if(!connected&&!connecting&&selected==null)StartScan();};
        Closed+=(_,_)=>StopScan();

        Button AddButton(System.Windows.Media.Geometry icon,string label,Action action)
        {
            var button=new ActionButton(icon, label) { Margin=new Thickness(0,0,6,4), Padding=new Thickness(9,5,9,5) };
            button.Click+=(_,_)=>action();
            actions.Children.Add(button);
            return button;
        }
    }

    public void UpdateConnection(bool isConnected,string? message,bool isConnecting=false,bool isAutomatic=false)
    {
        connected=isConnected;
        connecting=isConnecting;
        automaticConnection=isAutomatic;
        status.Text=message??"";
        RefreshButtons();
    }

    public void ConnectionLost(string message)
    {
        connected=false;
        if(!connecting)status.Text=message;
        RefreshButtons();
    }

    void RefreshButtons()
    {
        scan.IsEnabled=!connecting||automaticConnection;
        connect.IsEnabled=scan.IsEnabled&&list.SelectedItem is BluetoothDeviceChoice;
        disconnect.IsEnabled=connecting||connected;
    }

    void StartScan()
    {
        StopScan();
        devices.Clear();
        names.Clear();
        var search=new BluetoothLEAdvertisementWatcher { ScanningMode=BluetoothLEScanningMode.Active };
        watcher=search;
        search.Received+=AdvertisementReceived;
        search.Stopped+=ScanStopped;
        try
        {
            search.Start();
            status.Text=UiText.Get(scanningMessage);
            scanTimer.Start();
            SearchStateChanged?.Invoke();
        }
        catch(Exception ex)
        {
            StopScan();
            status.Text=UiText.Get(scanErrorMessage)+" "+ex.Message;
        }
    }

    void StopScan()
    {
        scanTimer.Stop();
        if(watcher is not { } search)return;
        watcher=null;
        search.Received-=AdvertisementReceived;
        search.Stopped-=ScanStopped;
        if(search.Status==BluetoothLEAdvertisementWatcherStatus.Started)search.Stop();
        SearchStateChanged?.Invoke();
    }

    void ScanStopped(BluetoothLEAdvertisementWatcher sender,BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if(args.Error==BluetoothError.Success)return;
        var error=args.Error;
        Dispatcher.BeginInvoke(()=>{
            if(!ReferenceEquals(sender,watcher))return;
            StopScan();
            status.Text=UiText.Get(scanErrorMessage)+" "+error;
        });
    }

    void AdvertisementReceived(BluetoothLEAdvertisementWatcher sender,BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name=args.Advertisement.LocalName;
        bool advertisesService=serviceFilter is Guid service&&args.Advertisement.ServiceUuids.Contains(service);
        if(!advertisesService&&string.IsNullOrWhiteSpace(name))return;
        ulong address=args.BluetoothAddress;
        var type=args.BluetoothAddressType;
        Dispatcher.BeginInvoke(()=>{
            if(!ReferenceEquals(sender,watcher))return;
            if(!string.IsNullOrWhiteSpace(name))names[address]=name;
            var existing=devices.FirstOrDefault(item=>item.Address==address);
            if(existing==null&&serviceFilter!=null&&!advertisesService)return;
            var item=new BluetoothDeviceChoice(address,names.GetValueOrDefault(address)??$"{Title} · {address:X12}",type);
            if(existing==null)devices.Add(item);
            else if(existing!=item)
            {
                bool selected=Equals(list.SelectedItem,existing);
                devices[devices.IndexOf(existing)]=item;
                if(selected)list.SelectedItem=item;
            }
        });
    }
}
