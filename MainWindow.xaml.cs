using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace SuitoRide;
public partial class MainWindow : Window
{
    record Pt(double Lat,double Lon,double Ele,double D);
    record Trainer(ulong Address,string Name);
    readonly List<Pt> route=[];
    readonly List<Trainer> devices=[];
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(1)};
    BluetoothLEAdvertisementWatcher? watcher;
    BluetoothLEDevice? device;
    GattDeviceService? service;
    GattCharacteristic? data, control;
    TaskCompletionSource<byte[]>? reply;
    readonly SemaphoreSlim gate=new(1,1);
    double distance,speed,lastGrade=double.NaN;
    DateTime lastData=DateTime.MinValue,lastTick;
    bool riding,demo,ticking;
    public MainWindow(){InitializeComponent();timer.Tick+=Tick;Closing+=HandleClosing;Loaded+=(_,_)=>{InitMap();RestoreCatalog();UpdateKeyStatus();AutoStartPanorama();};Closed+=(_,_)=>{sharedRide?.Shutdown();watcher?.Stop();DisconnectCore();};}
    void LoadRoute(object s,RoutedEventArgs e)
    {
        if(riding||ticking){Status.Text="Nejprve zastavte jízdu.";return;}
        var dlg=new OpenFileDialog{Filter="GPX trasa (*.gpx)|*.gpx"};
        if(dlg.ShowDialog()!=true)return;
        LoadRouteFile(dlg.FileName);
    }
    void LoadRouteFile(string path)
    {
        if(SharedRouteLocked())return;
        try {
            var doc=XDocument.Load(path);
            var seg=doc.Descendants().Where(x=>x.Name.LocalName=="trkseg").ToList();
            if(seg.Count>1)throw new Exception("Použijte souvislou trasu s jedním úsekem.");
            var nodes=doc.Descendants().Where(x=>x.Name.LocalName=="trkpt").ToList();
            if(nodes.Count==0)nodes=doc.Descendants().Where(x=>x.Name.LocalName=="rtept").ToList();
            var points=new List<Pt>();
            foreach(var n in nodes){
                double lat=double.Parse((string?)n.Attribute("lat")??"",CultureInfo.InvariantCulture);
                double lon=double.Parse((string?)n.Attribute("lon")??"",CultureInfo.InvariantCulture);
                double ele=double.Parse(n.Elements().FirstOrDefault(x=>x.Name.LocalName=="ele")?.Value??"",CultureInfo.InvariantCulture);
                if(!double.IsFinite(lat)||!double.IsFinite(lon)||!double.IsFinite(ele)||Math.Abs(lat)>90||Math.Abs(lon)>180)throw new Exception("Neplatné souřadnice nebo výška.");
                double d=points.Count==0?0:points[^1].D+Dist(points[^1].Lat,points[^1].Lon,lat,lon);
                if(points.Count==0||d>points[^1].D+0.000001)points.Add(new(lat,lon,ele,d));
            }
            if(points.Count<2||points[^1].D<10)throw new Exception("Trasa musí mít alespoň dva různé body s výškou.");
            riding=false;timer.Stop();SetRideMode(false);route.Clear();route.AddRange(points);distance=0;
            routeSourcePath=System.IO.Path.GetFullPath(path);
            RouteName.Text=System.IO.Path.GetFileNameWithoutExtension(path);
            RouteInfo.Text=$"{route[^1].D/1000:F1} km · {route.Count} bodů · výška {route.Min(p=>p.Ele):F0}–{route.Max(p=>p.Ele):F0} m";
            Status.Text="Trasa načtena.";lastStreetDistance=double.NaN;UpdateView();SendRoute();UpdateStreetView(true);
        }catch(Exception ex){MessageBox.Show("GPX se nepodařilo načíst. Každý bod musí obsahovat výšku.\n"+ex.Message,"Načtení trasy");}
    }
    static double Dist(double a,double b,double c,double d){double r=Math.PI/180,x=(c-a)*r,y=(d-b)*r,h=Math.Pow(Math.Sin(x/2),2)+Math.Cos(a*r)*Math.Cos(c*r)*Math.Pow(Math.Sin(y/2),2);return 12742000*Math.Asin(Math.Sqrt(Math.Clamp(h,0,1)));}
    double Elev(double d){d=Math.Clamp(d,0,route[^1].D);int i=route.FindIndex(p=>p.D>=d);if(i<=0)return route[0].Ele;var a=route[i-1];var b=route[i];return a.Ele+(b.Ele-a.Ele)*(d-a.D)/(b.D-a.D);}
    double Slope(){double a=Math.Max(0,distance-25),b=Math.Min(route[^1].D,distance+25);return b>a?Math.Clamp(100*(Elev(b)-Elev(a))/(b-a),-10,15):0;}
    void DrawProfile(object? s,SizeChangedEventArgs? e){
        Profile.Children.Clear();if(route.Count<2||Profile.ActualWidth<=0)return;
        double min=route.Min(p=>p.Ele),max=route.Max(p=>p.Ele),h=Profile.ActualHeight-20;
        var line=new Polyline{Stroke=Brushes.MediumAquamarine,StrokeThickness=2};
        foreach(var p in route)line.Points.Add(new Point(p.D/route[^1].D*Profile.ActualWidth,10+h*(1-(p.Ele-min)/Math.Max(1,max-min))));
        Profile.Children.Add(line);Profile.Children.Add(new Line{X1=distance/route[^1].D*Profile.ActualWidth,X2=distance/route[^1].D*Profile.ActualWidth,Y1=0,Y2=Profile.ActualHeight,Stroke=Brushes.White,StrokeThickness=2});
    }
    void Scan(object s,RoutedEventArgs e){
        watcher?.Stop();devices.Clear();Devices.ItemsSource=null;Devices.ItemsSource=devices;
        watcher=new BluetoothLEAdvertisementWatcher{ScanningMode=BluetoothLEScanningMode.Active};
        watcher.Received+=(_,a)=>{var name=a.Advertisement.LocalName;if(string.IsNullOrWhiteSpace(name))return;Dispatcher.BeginInvoke(()=>{if(devices.Any(x=>x.Address==a.BluetoothAddress))return;devices.Add(new(a.BluetoothAddress,name));Devices.Items.Refresh();});};
        watcher.Start();Status.Text="Vyhledávám Bluetooth zařízení. Zapněte Suito a vyberte ho ze seznamu.";
    }
    async void Connect(object s,RoutedEventArgs e){
        if(Devices.SelectedItem is not Trainer selected){Status.Text="Nejprve vyberte zařízení.";return;}
        try{
            watcher?.Stop();DisconnectCore();Status.Text="Připojuji trenažér…";
            device=await BluetoothLEDevice.FromBluetoothAddressAsync(selected.Address)??throw new Exception("Zařízení není dostupné.");
            device.ConnectionStatusChanged+=ConnectionChanged;
            var ss=await device.GetGattServicesForUuidAsync(Guid.Parse("00001826-0000-1000-8000-00805f9b34fb"),BluetoothCacheMode.Uncached);
            if(ss.Status!=GattCommunicationStatus.Success||ss.Services.Count==0)throw new Exception("Zařízení nenabízí FTMS.");
            service=ss.Services[0];var cs=await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if(cs.Status!=GattCommunicationStatus.Success)throw new Exception("Službu FTMS nelze načíst.");
            data=cs.Characteristics.FirstOrDefault(c=>c.Uuid==Guid.Parse("00002ad2-0000-1000-8000-00805f9b34fb"));
            control=cs.Characteristics.FirstOrDefault(c=>c.Uuid==Guid.Parse("00002ad9-0000-1000-8000-00805f9b34fb"));
            if(data==null||control==null)throw new Exception("Chybí data nebo ovládání FTMS.");
            data.ValueChanged+=DataChanged;control.ValueChanged+=ControlChanged;
            if(await data.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)!=GattCommunicationStatus.Success||await control.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Indicate)!=GattCommunicationStatus.Success)throw new Exception("Nepodařilo se zapnout příjem dat.");
            await Command([0]);Status.Text=$"Připojeno: {selected.Name}.";
        }catch(Exception ex){DisconnectCore();Status.Text="Připojení selhalo: "+ex.Message;}
    }
    void ConnectionChanged(BluetoothLEDevice s,object e){if(s.ConnectionStatus==BluetoothConnectionStatus.Disconnected)Dispatcher.BeginInvoke(()=>{if(ReferenceEquals(device,s)){riding=false;timer.Stop();SetRideMode(false);Status.Text="Trenažér se odpojil. Jízda zastavena."+EndRide("Přerušeno");}});}
    void ControlChanged(GattCharacteristic s,GattValueChangedEventArgs e){var b=new byte[e.CharacteristicValue.Length];DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(b);reply?.TrySetResult(b);}
    async Task Command(byte[] b){
        await gate.WaitAsync();try{
            if(control==null)throw new Exception("Trenažér není připojen.");
            reply=new(TaskCreationOptions.RunContinuationsAsynchronously);using var w=new DataWriter();w.WriteBytes(b);
            if(await control.WriteValueAsync(w.DetachBuffer(),GattWriteOption.WriteWithResponse)!=GattCommunicationStatus.Success)throw new Exception("Zápis příkazu selhal.");
            var r=await reply.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(r.Length<3||r[0]!=0x80||r[1]!=b[0]||r[2]!=1)throw new Exception("Trenažér příkaz odmítl nebo nepodporuje.");
        }finally{reply=null;gate.Release();}
    }
    void DataChanged(GattCharacteristic s,GattValueChangedEventArgs e){
        var b=new byte[e.CharacteristicValue.Length];DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(b);
        try{if(b.Length<2)return;int f=BitConverter.ToUInt16(b,0),i=2;double? v=null,c=null,p=null;
            if((f&1)==0){v=BitConverter.ToUInt16(b,i)/100.0;i+=2;}if((f&2)!=0)i+=2;
            if((f&4)!=0){c=BitConverter.ToUInt16(b,i)/2.0;i+=2;}if((f&8)!=0)i+=2;if((f&16)!=0)i+=3;if((f&32)!=0)i+=2;
            if((f&64)!=0)p=BitConverter.ToInt16(b,i);
            Dispatcher.BeginInvoke(()=>{if(v.HasValue){speed=v.Value;lastData=DateTime.UtcNow;Speed.Text=$"{speed:F1} km/h";}if(c.HasValue)Cadence.Text=$"{c:F0} ot/min";if(p.HasValue){Power.Text=$"{p:F0} W";currentPower=p.Value;powerReceived=DateTime.UtcNow;}});
        }catch(ArgumentException){}
    }
    async void StartRide(object s,RoutedEventArgs e){
        if(route.Count<2){Status.Text="Nejprve načtěte GPX.";return;}
        if(control==null||device?.ConnectionStatus!=BluetoothConnectionStatus.Connected){Status.Text="Nejprve připojte trenažér.";return;}
        if(riding||ticking||countdown!=null||starting)return;if(pendingRide!=null){Status.Text="Nejprve ulož čekající jízdu.";return;}starting=true;try{if(!await PrepareCountdown())return;if(control==null||device?.ConnectionStatus!=BluetoothConnectionStatus.Connected){Status.Text="Trenažér není připojen.";return;}await Command([7]);demo=false;BeginRide();}catch(Exception ex){Status.Text="Jízdu nelze spustit: "+ex.Message;}finally{starting=false;}
    }
    async void StartDemo(object s,RoutedEventArgs e){if(route.Count<2){Status.Text="Nejprve načtěte GPX.";return;}if(riding||ticking||countdown!=null||starting)return;starting=true;try{if(!await PrepareCountdown())return;demo=true;BeginRide();}finally{starting=false;}}
    void BeginRide(){if(distance>=route[^1].D){distance=0;lastStreetDistance=double.NaN;UpdateStreetView(true);}lastGrade=double.NaN;lastTick=DateTime.UtcNow;session=demo?null:new RideSession(RouteName.Text,distance);currentPower=null;powerReceived=DateTime.MinValue;riding=true;SetRideMode(true);UpdateStreetView(true);timer.Start();PlayCountdownSound(true);Status.Text=demo?"Ukázková jízda: odpor se nemění.":"Jízda zahájena.";}
    async void Tick(object? s,EventArgs e){
        if(!riding||ticking)return;ticking=true;try{
            var now=DateTime.UtcNow;double dt=Math.Clamp((now-lastTick).TotalSeconds,0,2);lastTick=now;
            if(demo)speed=20;else if((now-lastData).TotalSeconds>5)speed=0;
            if(session!=null && currentPower.HasValue && (now-powerReceived).TotalSeconds<=5)session.AddPower(currentPower.Value,dt);
            distance=Math.Min(route[^1].D,distance+speed/3.6*dt);double grade=Slope();UpdateView();
            if(!demo&&(double.IsNaN(lastGrade)||Math.Abs(grade-lastGrade)>=0.5)){
                short raw=(short)Math.Round(grade*100);await Command([0x11,0,0,(byte)(raw&255),(byte)((raw>>8)&255),40,51]);lastGrade=grade;
            }
            if(riding && distance>=route[^1].D){riding=false;timer.Stop();SetRideMode(false);string saved=EndRide("Dokončeno");if(!demo)await Command([8,1]);Status.Text="Trasa dokončena."+saved;}
        }catch(Exception ex){riding=false;timer.Stop();SetRideMode(false);Status.Text="Jízda zastavena: "+ex.Message+EndRide("Přerušeno");}finally{ticking=false;}
    }
    void UpdateView(){if(route.Count<2)return;Progress.Value=distance/route[^1].D*100;Grade.Text=$"{Slope():F1} %";RideInfo.Text=$"Ujeto {distance/1000:F2} z {route[^1].D/1000:F2} km"+(demo?" · ukázková jízda":"");if(demo)Speed.Text=$"{speed:F1} km/h";DrawProfile(null,null);SendPosition();}
    async void StopRide(object s,RoutedEventArgs e){if(countdown!=null){countdown.Cancel();Status.Text="Odpočítávání zrušeno.";return;}bool real=riding&&!demo;riding=false;timer.Stop();SetRideMode(false);Status.Text="Jízda zastavena."+EndRide("Ukončeno");if(real)try{await Command([8,1]);}catch(Exception ex){Status.Text="Jízda zastavena, trenažér nepotvrdil zastavení: "+ex.Message;}}
    void Disconnect(object s,RoutedEventArgs e){DisconnectCore();Status.Text="Trenažér odpojen.";}
    void DisconnectCore(){EndRide("Přerušeno");riding=false;timer.Stop();SetRideMode(false);speed=0;lastData=DateTime.MinValue;if(data!=null)data.ValueChanged-=DataChanged;if(control!=null)control.ValueChanged-=ControlChanged;if(device!=null)device.ConnectionStatusChanged-=ConnectionChanged;service?.Dispose();device?.Dispose();service=null;device=null;data=null;control=null;}

    bool mapReady;
    string? catalogFolder;
    readonly string settingsFile=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SuitoRide","catalog.txt");
    async void InitMap()
    {
        try {
            await MapView.EnsureCoreWebView2Async();
            MapView.CoreWebView2.SetVirtualHostNameToFolderMapping("suitoride.local",System.IO.Path.Combine(AppContext.BaseDirectory,"map"),Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            MapView.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
            MapView.CoreWebView2.WebMessageReceived+=(_,e)=>{
                if(e.TryGetWebMessageAsString()=="ready"){mapReady=true;MapStatus.Text="Mapa: OpenStreetMap. Poloha vychází z ujeté vzdálenosti.";SendRoute();SendMap(new{type="rotate",enabled=RotateMap.IsChecked==true});SendMap(new{type="ride-layout",active=riding});foreach(var other in otherRiders.Values.ToArray())ShowOtherRider(other);UpdateStreetView(true);}
            };
            MapView.CoreWebView2.NavigationCompleted+=(_,e)=>{if(!e.IsSuccess)MapStatus.Text="Mapu se nepodařilo načíst. Zkontrolujte připojení a WebView2 Runtime.";};
            MapView.CoreWebView2.NewWindowRequested+=(_,e)=>{e.Handled=true;try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri){UseShellExecute=true});}catch{}};
            MapView.Source=new Uri("https://suitoride.local/index.html");
        }catch(Exception ex){MapStatus.Text="Mapu nelze spustit: "+ex.Message;}
    }
    void SendMap(object message){if(mapReady) MapView.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(message));}
    void SendRoute(){if(route.Count<2)return;SendMap(new{type="route",points=route.Select(p=>new[]{p.Lat,p.Lon})});SendPosition();}
    (double Lat,double Lon) Position()=>PositionAt(distance);
    void SendPosition(){if(route.Count<2)return;var p=Position();SendMap(new{type="position",lat=p.Lat,lon=p.Lon,heading=Heading()});sharedRide?.Publish(distance,riding);UpdateStreetView();}
    void FitMap(object s,RoutedEventArgs e)=>SendMap(new{type="fit"});
    void FollowRider(object s,RoutedEventArgs e)=>SendMap(new{type="follow"});
    void OpenStreetView(object s,RoutedEventArgs e)
    {
        if(route.Count<2){MapStatus.Text="Nejprve načtěte trasu.";return;}
        var p=Position();
        var url=FormattableString.Invariant($"https://www.google.com/maps/@?api=1&map_action=pano&viewpoint={p.Lat:F6}%2C{p.Lon:F6}&heading={Heading():F0}");
        try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true});}
        catch(Exception ex){MapStatus.Text="Panoramatický pohled nelze otevřít: "+ex.Message;}
    }
    void RestoreCatalog()
    {
        try{if(File.Exists(settingsFile)){catalogFolder=File.ReadAllText(settingsFile).Trim();RefreshCatalogCore();}}
        catch(Exception ex){CatalogInfo.Text="Katalog nelze načíst: "+ex.Message;}
    }
    void ChooseFolder(object s,RoutedEventArgs e)
    {
        var dlg=new OpenFolderDialog{Title="Vyberte složku s GPX trasami",Multiselect=false};
        if(dlg.ShowDialog()!=true)return;
        catalogFolder=dlg.FolderName;
        try{Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsFile)!);File.WriteAllText(settingsFile,catalogFolder);}
        catch(Exception ex){CatalogInfo.Text="Složku nelze uložit: "+ex.Message;return;}
        RefreshCatalogCore();
    }
    void RefreshCatalog(object s,RoutedEventArgs e)=>RefreshCatalogCore();
    void RefreshCatalogCore()
    {
        if(string.IsNullOrWhiteSpace(catalogFolder)){CatalogInfo.Text="Nejprve vyberte složku tras.";return;}
        try{
            var files=new DirectoryInfo(catalogFolder).EnumerateFiles().Where(f=>string.Equals(f.Extension,".gpx",StringComparison.OrdinalIgnoreCase)).OrderBy(f=>f.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
            Catalog.ItemsSource=files;if(files.Count>0)Catalog.SelectedIndex=0;
            CatalogInfo.Text=$"Katalog: {catalogFolder} · {files.Count} tras";
        }catch(Exception ex){Catalog.ItemsSource=null;CatalogInfo.Text="Katalog nelze načíst: "+ex.Message;}
    }
    void LoadCatalog(object s,RoutedEventArgs e)
    {
        if(riding||ticking){Status.Text="Nejprve zastavte jízdu.";return;}
        if(Catalog.SelectedItem is not FileInfo f){Status.Text="Nejprve vyberte trasu v katalogu.";return;}
        LoadRouteFile(f.FullName);
    }

    string? streetKey;
    string streetProvider="mapy";
    double lastStreetDistance=double.NaN;
    (double Lat,double Lon) PositionAt(double d)
    {
        d=Math.Clamp(d,0,route[^1].D);
        int i=route.FindIndex(p=>p.D>=d);
        if(i<=0)return (route[0].Lat,route[0].Lon);
        var a=route[i-1];var b=route[i];double t=Math.Clamp((d-a.D)/(b.D-a.D),0,1);
        double delta=b.Lon-a.Lon;if(delta>180)delta-=360;else if(delta< -180)delta+=360;
        double lon=a.Lon+delta*t;if(lon>180)lon-=360;else if(lon< -180)lon+=360;
        return (a.Lat+(b.Lat-a.Lat)*t,lon);
    }
    double Heading()
    {
        if(route.Count<2)return 0;
        int next=route.FindIndex(p=>p.D>distance+1e-8);
        if(next<1)next=route.Count-1;
        var a=route[next-1];var b=route[next];
        double rad=Math.PI/180,lat1=a.Lat*rad,lat2=b.Lat*rad,dlon=(b.Lon-a.Lon)*rad;
        double y=Math.Sin(dlon)*Math.Cos(lat2);
        double x=Math.Cos(lat1)*Math.Sin(lat2)-Math.Sin(lat1)*Math.Cos(lat2)*Math.Cos(dlon);
        return (Math.Atan2(y,x)/rad+360)%360;
    }
    void SetMapRotation(object s,RoutedEventArgs e)=>SendMap(new{type="rotate",enabled=RotateMap.IsChecked==true});
    void EnableStreetView(object s,RoutedEventArgs e)
    {
        var provider=SelectedPanoramaProvider();
        var key=GoogleKey.Password.Trim();
        try {
            if(string.IsNullOrWhiteSpace(key)) key=PanoramaKeyStore.Load(provider) ?? "";
            else PanoramaKeyStore.Save(provider,key);
        } catch(Exception ex) { StreetStatus.Text="Klíč se nepodařilo uložit nebo načíst: "+ex.Message;return; }
        if(string.IsNullOrWhiteSpace(key)){StreetStatus.Text="Zadejte API klíč zvoleného poskytovatele.";return;}
        streetKey=key;streetProvider=provider;GoogleKey.Clear();lastStreetDistance=double.NaN;UpdateKeyStatus();
        string? preferenceError=null;
        try { PanoramaKeyStore.RememberProvider(provider); }
        catch { preferenceError=" Poskytovatele se nepodařilo uložit pro příští spuštění."; }
        StreetStatus.Text="Panorama zapnuto. Obnovuje se po 20 m; snímky nemusejí být dostupné na celé trase.";
        if(route.Count>=2)UpdateStreetView(true);
        else StreetStatus.Text="Panorama zapnuto. Pro zobrazení načtěte GPX trasu.";
        if(preferenceError!=null)StreetStatus.Text+=preferenceError;
    }
    void DisableStreetView(object s,RoutedEventArgs e)
    {
        streetKey=null;GoogleKey.Clear();lastStreetDistance=double.NaN;
        SendMap(new{type="streetview-off"});StreetStatus.Text="Street View je vypnuté.";
    }
    void UpdateStreetView(bool force=false)
    {
        if(!mapReady||streetKey==null||route.Count<2)return;
        if(!force&&(!riding||(!double.IsNaN(lastStreetDistance)&&Math.Abs(distance-lastStreetDistance)<20)))return;
        var p=Position();
        SendMap(new{type="streetview",provider=streetProvider,key=streetKey,lat=p.Lat,lon=p.Lon,heading=Heading()});
        lastStreetDistance=distance;
    }

    string SelectedPanoramaProvider()=>PanoramaProvider.SelectedIndex==0?"mapy":"google";
    void PanoramaProviderChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(!IsLoaded)return;
        GoogleKey.Clear();
        UpdateKeyStatus();
    }
    void UpdateKeyStatus()
    {
        try {
            KeyStatus.Text=PanoramaKeyStore.Exists(SelectedPanoramaProvider())
                ? "Pro zvoleného poskytovatele je klíč uložen. Ponech prázdné pro jeho použití nebo zadej nový."
                : "Pro zvoleného poskytovatele zatím není uložen klíč.";
        } catch { KeyStatus.Text="Stav uloženého klíče se nepodařilo zjistit."; }
    }
    void ForgetPanoramaKey(object s,RoutedEventArgs e)
    {
        var provider=SelectedPanoramaProvider();
        try {
            PanoramaKeyStore.Delete(provider); GoogleKey.Clear();
            if(streetProvider==provider && streetKey!=null)DisableStreetView(s,e);
            UpdateKeyStatus(); StreetStatus.Text="Uložený klíč zvoleného poskytovatele byl odstraněn.";
        } catch(Exception ex) { StreetStatus.Text="Klíč nelze odstranit: "+ex.Message; }
    }
    void AutoStartPanorama()
    {
        try {
            var provider=PanoramaKeyStore.LastProvider();
            if(provider==null){StreetStatus.Text="Pro automatické zapnutí zadejte a uložte API klíč.";return;}
            PanoramaProvider.SelectedIndex=provider=="mapy"?0:1;
            EnableStreetView(this,new RoutedEventArgs());
        } catch(Exception ex) { StreetStatus.Text="Panorama se nepodařilo automaticky zapnout: "+ex.Message; }
    }    void SetRideMode(bool active)
    {
        SetupPanel.Visibility=active?Visibility.Collapsed:Visibility.Visible;
        SendMap(new{type="ride-layout",active});
        sharedRide?.Publish(distance,active);
        StartButton.Visibility=active?Visibility.Collapsed:Visibility.Visible;
        DemoButton.Visibility=active?Visibility.Collapsed:Visibility.Visible;
        StopButton.Visibility=active?Visibility.Visible:Visibility.Collapsed;
    }
    readonly RideHistoryStore historyStore=new();
    RideSession? session;
    RideRecord? pendingRide;
    double? currentPower;
    DateTime powerReceived=DateTime.MinValue;
    string EndRide(string result)
    {
        if(session==null)return "";
        var record=session.Finish(distance,result);
        session=null;
        if(record.DistanceMeters<0.1)return "";
        pendingRide=record;
        try {
            historyStore.Save(record); pendingRide=null; RetrySaveButton.Visibility=Visibility.Collapsed;
            return " Jízda uložena do historie.";
        } catch(Exception) {
            RetrySaveButton.Visibility=Visibility.Visible;
            return " Jízdu se nepodařilo uložit. Použij tlačítko Uložit čekající jízdu.";
        }
    }
    void RetrySaveRide(object s,RoutedEventArgs e)
    {
        if(pendingRide==null)return;
        try { historyStore.Save(pendingRide);pendingRide=null;RetrySaveButton.Visibility=Visibility.Collapsed;Status.Text="Jízda uložena do historie."; }
        catch(Exception ex) { Status.Text="Jízdu nelze uložit: "+ex.Message; }
    }
    void ShowHistory(object s,RoutedEventArgs e) => new HistoryWindow(historyStore) { Owner=this }.ShowDialog();
    void HandleClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        countdown?.Cancel();
        EndRide("Přerušeno");
        if(pendingRide!=null) {
            riding=false;timer.Stop();SetRideMode(false);
            e.Cancel=true;
            Status.Text="Jízdu se nepodařilo uložit. Zkontroluj přístup k datům a zkus uložení znovu.";
            MessageBox.Show(Status.Text,"Neuložená jízda",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }

    string? routeSourcePath;
    List<Pt> CreateEditedRoute(double start, double end, bool reverse)
    {
        if(route.Count<2 || start<0 || end>route[^1].D || end-start<10)throw new ArgumentException("Neplatný úsek trasy.");
        var first=PositionAt(start); var last=PositionAt(end);
        var selected=new List<Pt>{new(first.Lat,first.Lon,Elev(start),0)};
        selected.AddRange(route.Where(p=>p.D>start && p.D<end));
        selected.Add(new(last.Lat,last.Lon,Elev(end),0));
        if(reverse)selected.Reverse();
        var result=new List<Pt>();double length=0;
        foreach(var point in selected)
        {
            if(result.Count>0){double step=Dist(result[^1].Lat,result[^1].Lon,point.Lat,point.Lon);if(step<0.000001)continue;length+=step;}
            result.Add(new(point.Lat,point.Lon,point.Ele,length));
        }
        if(result.Count<2 || length<1)throw new InvalidDataException("Vybraný úsek neobsahuje dostatek různých bodů.");
        return result;
    }
    void EditRoute(object s,RoutedEventArgs e)
    {
        if(riding||ticking){Status.Text="Nejprve zastavte jízdu.";return;}
        if(route.Count<2){Status.Text="Nejprve načtěte GPX trasu.";return;}
        if(SharedRouteLocked())return;
        var dialog=new RouteEditWindow(route[^1].D,route.Select(p=>(p.D,p.Ele)).ToList()){Owner=this};
        if(dialog.ShowDialog()!=true)return;
        try {
            var edited=CreateEditedRoute(dialog.StartMeters,dialog.EndMeters,dialog.Reverse);
            route.Clear();route.AddRange(edited);distance=0;lastStreetDistance=double.NaN;
            RouteName.Text=RouteName.Text+" – upravená";
            RouteInfo.Text=$"{route[^1].D/1000:F1} km · {route.Count} bodů · výška {route.Min(p=>p.Ele):F0}–{route.Max(p=>p.Ele):F0} m";
            UpdateView();SendRoute();UpdateStreetView(true);
            Status.Text="Upravená trasa je připravena k jízdě.";
            if(MessageBox.Show(this,"Chceš upravenou trasu uložit jako nový GPX soubor?","Upravit trasu",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            var save=new SaveFileDialog{Filter="GPX trasa (*.gpx)|*.gpx",DefaultExt=".gpx",AddExtension=true,FileName=RouteName.Text+".gpx",OverwritePrompt=true};
            if(save.ShowDialog(this)!=true){Status.Text="Trasa je upravená pro tuto jízdu; GPX nebyl uložen.";return;}
            if(routeSourcePath!=null && string.Equals(System.IO.Path.GetFullPath(save.FileName),System.IO.Path.GetFullPath(routeSourcePath),StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this,"Vyber jiný soubor, aby původní GPX zůstal zachován.","Uložit trasu");Status.Text="GPX nebyl uložen.";return; }
            SaveEditedGpx(save.FileName);
            routeSourcePath=System.IO.Path.GetFullPath(save.FileName);
            Status.Text="Upravená trasa byla uložena jako nový GPX.";
            if(!string.IsNullOrEmpty(catalogFolder))RefreshCatalogCore();
        } catch(Exception ex) { MessageBox.Show(this,"Úpravu nebo uložení trasy se nepodařilo dokončit: "+ex.Message,"Upravit trasu"); }
    }

    CancellationTokenSource? countdown;
    bool starting;
    async Task<bool> PrepareCountdown()
    {
        if(countdown!=null)return false;
        var cancel=new CancellationTokenSource();countdown=cancel;
        StartButton.IsEnabled=false;DemoButton.IsEnabled=false;SetupPanel.IsEnabled=false;
        StopButton.Visibility=Visibility.Visible;
        CountdownWindow? overlay=null;
        try {
            overlay=new CountdownWindow(()=>{if(!cancel.IsCancellationRequested){Status.Text="Odpočítávání zrušeno.";cancel.Cancel();}});
            if(IsVisible)overlay.Owner=this;
            overlay.Show();
            for(int remaining=10;remaining>0;remaining--) {
                Status.Text=$"Jízda začne za {remaining} s…";
                overlay.SetRemaining(remaining);
                if(remaining<=3)PlayCountdownSound(false);
                await Task.Delay(1000,cancel.Token);
            }
            cancel.Token.ThrowIfCancellationRequested();
            return true;
        } catch(OperationCanceledException) { return false; }
        catch(Exception ex) { Status.Text="Odpočet se nepodařilo spustit: "+ex.Message;return false; }
        finally {
            overlay?.Dismiss();
            if(ReferenceEquals(countdown,cancel))countdown=null;
            cancel.Dispose();StartButton.IsEnabled=true;DemoButton.IsEnabled=true;SetupPanel.IsEnabled=true;
            if(!riding)StopButton.Visibility=Visibility.Collapsed;
        }
    }
    void SaveEditedGpx(string path)
    {
        XNamespace ns="http://www.topografix.com/GPX/1/1";
        var doc=new XDocument(new XDeclaration("1.0","utf-8",null),
            new XElement(ns+"gpx",new XAttribute("version","1.1"),new XAttribute("creator","Suito Ride"),
                new XElement(ns+"trk",new XElement(ns+"name",RouteName.Text),
                    new XElement(ns+"trkseg",route.Select(p=>new XElement(ns+"trkpt",
                        new XAttribute("lat",p.Lat),new XAttribute("lon",p.Lon),new XElement(ns+"ele",p.Ele)))))));
        doc.Save(path);
    }
    SharedRideWindow? sharedRide;
    string? sharedRouteHash;
    readonly Dictionary<string,SharedRider> otherRiders=[];
    string RouteFingerprint()
    {
        var text=string.Join(";",route.Select(p=>FormattableString.Invariant($"{p.Lat:R},{p.Lon:R},{p.Ele:R}")));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }
    void OpenSharedRide(object s,RoutedEventArgs e)
    {
        if(route.Count<2){Status.Text="Nejprve načti GPX trasu pro společnou jízdu.";return;}
        var hash=RouteFingerprint();
        if(sharedRide!=null && sharedRouteHash!=hash){sharedRide.Shutdown();sharedRide=null;}
        if(sharedRide==null){
            sharedRouteHash=hash;
            sharedRide=new SharedRideWindow(hash,ShowOtherRider,RemoveOtherRider,ClearOtherRiders,()=>sharedRide?.Publish(distance,riding)){Owner=this};
        }
        if(!sharedRide.IsVisible)sharedRide.Show();else sharedRide.Activate();
    }
    void ShowOtherRider(SharedRider other)
    {
        if(route.Count<2 || !double.IsFinite(other.Distance))return;
        otherRiders[other.Id]=other;
        var p=PositionAt(Math.Clamp(other.Distance,0,route[^1].D));
        SendMap(new{type="peer",id=other.Id,name=other.Name,lat=p.Lat,lon=p.Lon,riding=other.Riding});
    }
    void RemoveOtherRider(string id){otherRiders.Remove(id);SendMap(new{type="peer-left",id});}
    void ClearOtherRiders(){otherRiders.Clear();SendMap(new{type="peer-clear"});}
    bool SharedRouteLocked()
    {
        if(sharedRide?.IsBusy!=true)return false;
        Status.Text="Pro změnu trasy se nejprve odpoj ze společné jízdy.";
        return true;
    }
    static void PlayCountdownSound(bool start)
    {
        try { if(start)System.Media.SystemSounds.Asterisk.Play();else System.Media.SystemSounds.Beep.Play(); }
        catch(Exception) { /* Odpočet funguje i bez dostupného zvukového zařízení. */ }
    }}
