using Microsoft.Win32;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace SuitoRide;
public partial class MainWindow : Window
{
    record Pt(double Lat,double Lon,double Ele,double D);
    readonly List<Pt> route=[];
    ElevationProfile? elevationProfile;
    BluetoothDeviceChoice? selectedTrainer;
    BluetoothDeviceWindow? trainerDialog;
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(1)};
    BluetoothLEDevice? device;
    GattDeviceService? service;
    GattCharacteristic? data, control;
    TaskCompletionSource<byte[]>? reply;
    readonly SemaphoreSlim gate=new(1,1);
    double distance,speed,lastGrade=double.NaN;
    DateTime lastData=DateTime.MinValue,lastTick;
    bool riding,demo,ticking;
    readonly Stopwatch rideClock=new();
    double rideStartDistance;
    public MainWindow(){InitializeComponent();InitializeProfileLegend();Language=System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);Title=$"Suito Ride {typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";timer.Tick+=Tick;Closing+=HandleClosing;Loaded+=(_,_)=>{InitializeHeartRate();RestoreRiderName();InitMap();RestoreCatalog();RestoreLastRoute();RestorePanoramaSpacing();AutoStartPanorama();trainerRestoreTask=RestoreTrainerAsync();heartRateRestoreTask=RestoreHeartRateAsync();};Closed+=(_,_)=>{sharedRide?.Shutdown();trainerConnection?.Cancel();heartRateAttempt?.Cancel();DisconnectHeartRate();DisconnectCore();};}
    bool installingUpdate;
    async void CheckForUpdates(object sender,RoutedEventArgs e)
    {
        if(sender is not System.Windows.Controls.Button button)return;
        button.IsEnabled=false;
        try
        {
            Status.Text=UiText.Get("Kontroluji aktualizace na GitHubu…");
            var current=typeof(MainWindow).Assembly.GetName().Version!;
            string? token=AppUpdates.LoadToken();
            AppUpdates.Update? update;
            try { update=await AppUpdates.FindAsync(current,token); }
            catch(AppUpdates.GitHubAccessException)
            {
                if(!IsLoaded)return;
                var access=new GitHubUpdateAccessWindow {Owner=this};
                if(access.ShowDialog()!=true){Status.Text=UiText.Get("Kontrola soukromého vydání vyžaduje přístup ke GitHubu.");return;}
                token=access.Token;
                try { update=await AppUpdates.FindAsync(current,token); }
                catch(AppUpdates.GitHubAccessException)
                {
                    Status.Text=UiText.Get("GitHub vydání nezpřístupnil. Ověř platnost tokenu a oprávnění Contents: Read.");
                    return;
                }
                AppUpdates.SaveToken(token);
            }
            if(!IsLoaded)return;
            if(update==null){Status.Text=UiText.Get("Používáš aktuální vydání Suito Ride.");return;}
            if(riding||starting||pendingRide!=null){Status.Text=UiText.Format($"Verze {update.Version} je dostupná. Instalace je možná po jízdě.");return;}
            if(MessageBox.Show(this,
                UiText.Format($"Stáhnout, nainstalovat a spustit Suito Ride {update.Version}? Nová verze se uloží do {AppUpdates.InstallFolder}."),
                "Aktualizace Suito Ride",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)
            {
                Status.Text=UiText.Format($"Verze {update.Version} je dostupná.");
                return;
            }
            installingUpdate=true;
            StartButton.IsEnabled=DemoButton.IsEnabled=false;
            Status.Text=UiText.Get("Stahuji a ověřuji aktualizaci…");
            string executable=await AppUpdates.InstallAsync(update,token);
            if(!IsLoaded)return;
            var process=Process.Start(new ProcessStartInfo(executable)
                {WorkingDirectory=System.IO.Path.GetDirectoryName(executable)!,UseShellExecute=true});
            if(process==null)throw new InvalidOperationException(UiText.Get("Novou verzi se nepodařilo spustit."));
            process.Dispose();
            installingUpdate=false;
            Close();
        }
        catch(Exception ex){Status.Text=UiText.Get("Aktualizace se nezdařila: ")+ex.Message;}
        finally
        {
            if(installingUpdate)StartButton.IsEnabled=DemoButton.IsEnabled=true;
            installingUpdate=false;
            button.IsEnabled=true;
        }
    }
    void LoadRoute(object s,RoutedEventArgs e)
    {
        if(riding||ticking){Status.Text=UiText.Get("Nejprve zastavte jízdu.");return;}
        var dlg=new OpenFileDialog{Filter="GPX trasa (*.gpx)|*.gpx"};
        if(dlg.ShowDialog()!=true)return;
        LoadRouteFile(dlg.FileName);
    }
    void LoadRouteFile(string path,bool restoring=false)
    {
        if(SharedRouteLocked())return;
        try {
            var doc=XDocument.Load(path);
            var seg=doc.Descendants().Where(x=>x.Name.LocalName=="trkseg").ToList();
            if(seg.Count>1)throw new Exception(UiText.Get("Použijte souvislou trasu s jedním úsekem."));
            var nodes=doc.Descendants().Where(x=>x.Name.LocalName=="trkpt").ToList();
            if(nodes.Count==0)nodes=doc.Descendants().Where(x=>x.Name.LocalName=="rtept").ToList();
            var points=new List<Pt>();
            foreach(var n in nodes){
                double lat=double.Parse((string?)n.Attribute("lat")??"",CultureInfo.InvariantCulture);
                double lon=double.Parse((string?)n.Attribute("lon")??"",CultureInfo.InvariantCulture);
                double ele=double.Parse(n.Elements().FirstOrDefault(x=>x.Name.LocalName=="ele")?.Value??"",CultureInfo.InvariantCulture);
                if(!double.IsFinite(lat)||!double.IsFinite(lon)||!double.IsFinite(ele)||Math.Abs(lat)>90||Math.Abs(lon)>180)throw new Exception(UiText.Get("Neplatné souřadnice nebo výška."));
                double d=points.Count==0?0:points[^1].D+Dist(points[^1].Lat,points[^1].Lon,lat,lon);
                if(points.Count==0||d>points[^1].D+0.000001)points.Add(new(lat,lon,ele,d));
            }
            if(points.Count<2||points[^1].D<10)throw new Exception(UiText.Get("Trasa musí mít alespoň dva různé body s výškou."));
            riding=false;timer.Stop();SetRideMode(false);SetRoute(points);distance=0;
            routeSourcePath=System.IO.Path.GetFullPath(path);
            routeHasUnsavedEdits=false;
            SyncCatalogSelection();
            RouteName.Text=System.IO.Path.GetFileNameWithoutExtension(path);
            UpdateRouteInfo();
            Status.Text=UiText.Get("Trasa načtena.");
            if(!restoring)RememberLastRoute(routeSourcePath);lastStreetDistance=double.NaN;UpdateView();SendRoute();UpdateStreetView(true);
        }catch(Exception ex){
            if(restoring)Status.Text=UiText.Get("Poslední GPX trasu se nepodařilo načíst: ")+ex.Message;
            else MessageBox.Show(UiText.Get("GPX se nepodařilo načíst. Každý bod musí obsahovat výšku.\n")+ex.Message,UiText.Get("Načtení trasy"));
        }
    }
    void SetRoute(List<Pt> points)
    {
        elevationProfile = new ElevationProfile(points.Select(p => (p.D, p.Ele)));
        ClearProfilePreview();
        ResetProfileTelemetry();
        route.Clear();
        route.AddRange(points);
    }
    static double Dist(double a,double b,double c,double d){double r=Math.PI/180,x=(c-a)*r,y=(d-b)*r,h=Math.Pow(Math.Sin(x/2),2)+Math.Cos(a*r)*Math.Cos(c*r)*Math.Pow(Math.Sin(y/2),2);return 12742000*Math.Asin(Math.Sqrt(Math.Clamp(h,0,1)));}
    double Elev(double d){d=Math.Clamp(d,0,route[^1].D);int i=route.FindIndex(p=>p.D>=d);if(i<=0)return route[0].Ele;var a=route[i-1];var b=route[i];return a.Ele+(b.Ele-a.Ele)*(d-a.D)/(b.D-a.D);}
    double SlopeAt(double meters){double a=Math.Max(0,meters-25),b=Math.Min(route[^1].D,meters+25);return b>a?Math.Clamp(100*(Elev(b)-Elev(a))/(b-a),-10,15):0;}
    void DrawProfile(object? s,SizeChangedEventArgs? e)
    {
        profilePositionMarker = null;
        DrawProfileRange(Profile,0,route.Count<2?0:route[^1].D);
        DrawZoomProfile();
    }
    void DrawZoomProfile()
    {
        if(route.Count<2){ZoomProfile.Children.Clear();return;}
        double meters = DisplayDistance;
        double start=Math.Max(0,meters-30),end=Math.Min(route[^1].D,meters+500);
        if(end-start<1)start=Math.Max(0,end-100);
        DrawProfileRange(ZoomProfile,start,end);
    }
    void DrawProfileRange(System.Windows.Controls.Canvas canvas,double start,double end)
    {
        canvas.Children.Clear();
        if(elevationProfile==null||canvas.ActualWidth<=0||canvas.ActualHeight<=0||end<=start)return;
        var points=elevationProfile.Sample(start,end,canvas.ActualWidth);
        double min=points.Min(p=>p.Elevation),max=points.Max(p=>p.Elevation),height=Math.Max(1,canvas.ActualHeight-12);
        double X(double d)=>(d-start)/(end-start)*canvas.ActualWidth;
        double Y(double ele)=>6+height*(1-(ele-min)/Math.Max(1,max-min));
        for(int i=1;i<points.Length;i++)
        {
            var a=points[i-1];var b=points[i];
            double middle=(a.Distance+b.Distance)/2,lo=Math.Max(0,middle-25),hi=Math.Min(route[^1].D,middle+25);
            double grade=100*(elevationProfile.At(hi)-elevationProfile.At(lo))/(hi-lo);
            canvas.Children.Add(new Line{X1=X(a.Distance),Y1=Y(a.Elevation),X2=X(b.Distance),Y2=Y(b.Elevation),Stroke=GradeBrush(grade),StrokeThickness=3});
        }
        bool isMainProfile = ReferenceEquals(canvas, Profile);
        if(isMainProfile)DrawProfileTelemetry(canvas, X, height);
        double meters = DisplayDistance;
        if(meters>=start&&meters<=end)
        {
            var marker = new Line{X1=X(meters),X2=X(meters),Y1=0,Y2=canvas.ActualHeight,Stroke=Brushes.White,StrokeThickness=2,IsHitTestVisible=false};
            canvas.Children.Add(marker);
            if(isMainProfile)profilePositionMarker = marker;
        }
    }
    void UpdateRideMetrics()
    {
        var elapsed=rideClock.Elapsed;
        Elapsed.Text=$"{(long)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        if(elapsed.TotalSeconds<5){SpeedTrend.Text="";return;}
        double average=Math.Max(0,distance-rideStartDistance)/elapsed.TotalSeconds*3.6;
        double delta=speed-average;
        SpeedTrend.Text=Math.Abs(delta)<0.1?"→":delta>0?"↑":"↓";
        SpeedTrend.Foreground=Math.Abs(delta)<0.1?Brushes.White:delta>0?Brushes.LightGreen:Brushes.Orange;
        SpeedTrend.ToolTip=UiText.Format($"Průměrná rychlost jízdy: {average:F1} km/h");
    }
    CancellationTokenSource? trainerConnection;
    bool trainerConnectingAutomatically, trainerRestoreSuperseded;
    Task? trainerRestoreTask;
    string? trainerConnectionActivity;

    void SetTrainerActivity(string? activity)
    {
        trainerConnectionActivity=activity;
        RefreshTrainerActivity();
    }

    void RefreshTrainerActivity() =>
        ShowBluetoothActivity(TrainerActivity,trainerDialog?.IsSearching==true ? "Hledám trenažér…" : trainerConnectionActivity);

    static void ShowBluetoothActivity(System.Windows.Controls.TextBlock indicator,string? activity)
    {
        indicator.Text=activity==null ? "" : UiText.Get(activity);
        indicator.Visibility=activity==null ? Visibility.Collapsed : Visibility.Visible;
    }
    string LastTrainerPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsFile)!, "last-trainer.json");
    async Task RestoreTrainerAsync()
    {
        try
        {
            selectedTrainer=BluetoothDeviceConnection.Load(LastTrainerPath);
            if(selectedTrainer!=null)await ConnectTrainerAsync(selectedTrainer,true);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SetTrainerConnectionStatus(UiText.Get("Poslední trenažér nelze obnovit. Můžeš ho vyhledat ručně."));
        }
    }

    void OpenTrainer(object sender,RoutedEventArgs e)
    {
        if(riding||starting)return;
        var dialog=new BluetoothDeviceWindow("Trenažér","Vyhledat trenažér",
            "Vyhledávám Bluetooth zařízení. Zapněte Suito a vyberte ho ze seznamu.","Žádné Bluetooth zařízení nebylo nalezeno.",
            "Vyberte trenažér ze seznamu.","Vyhledávání Bluetooth zařízení selhalo.","Připojuji trenažér…",
            selected:selectedTrainer) { Owner=this };
        trainerDialog=dialog;
        dialog.UpdateConnection(control!=null,TrainerButton.ToolTip as string,trainerConnection!=null,trainerConnectingAutomatically);
        dialog.ManualScanRequested+=CancelAutomaticTrainerConnection;
        dialog.SearchStateChanged+=RefreshTrainerActivity;
        dialog.ConnectRequested+=async selected=>{
            CancelAutomaticTrainerConnection();
            if(trainerRestoreTask is { IsCompleted:false } restore)await restore;
            if(ReferenceEquals(trainerDialog,dialog))await ConnectTrainerAsync(selected,false);
        };
        dialog.DisconnectRequested+=DisconnectTrainer;
        try { dialog.ShowDialog(); }
        finally
        {
            trainerDialog=null;
            RefreshTrainerActivity();
        }
    }

    void CancelAutomaticTrainerConnection()
    {
        if(!trainerConnectingAutomatically)return;
        trainerRestoreSuperseded=true;
        SetTrainerActivity(null);
        trainerConnection?.Cancel();
        TrainerButton.ToolTip=null;
        if(!riding||!demo)Status.Text=UiText.Get("Pokus o připojení skončil. Trenažér můžeš připojit ručně.");
    }

    void SetTrainerConnectionStatus(string message,bool connecting=false)
    {
        if(!riding||!demo)Status.Text=message;
        TrainerButton.ToolTip=message;
        trainerDialog?.UpdateConnection(control!=null,message,connecting,connecting&&trainerConnectingAutomatically);
    }

    async Task ConnectTrainerAsync(BluetoothDeviceChoice selected,bool automatic)
    {
        if(trainerConnection!=null || riding || starting)return;
        using var attempt=new CancellationTokenSource();
        if(!automatic)attempt.CancelAfter(TimeSpan.FromSeconds(20));
        trainerConnection=attempt;
        trainerConnectingAutomatically=automatic;
        var cancellation=attempt.Token;
        try{
            selectedTrainer=selected;
            DisconnectCore();
            SetTrainerConnectionStatus(automatic?UiText.Format($"Připojuji poslední trenažér: {selected.Name}…"):UiText.Get("Připojuji trenažér…"),true);
            SetTrainerActivity(automatic ? "Hledám trenažér…" : "Připojuji trenažér…");
            device=await BluetoothDeviceConnection.OpenAsync(selected,automatic,cancellation);
            cancellation.ThrowIfCancellationRequested();
            SetTrainerActivity("Připojuji trenažér…");
            if(automatic)attempt.CancelAfter(TimeSpan.FromSeconds(20));
            device.ConnectionStatusChanged+=ConnectionChanged;
            var ss=await device.GetGattServicesForUuidAsync(Guid.Parse("00001826-0000-1000-8000-00805f9b34fb"),BluetoothCacheMode.Uncached).AsTask(cancellation);
            if(ss.Status!=GattCommunicationStatus.Success||ss.Services.Count==0)throw new Exception(UiText.Get("Zařízení nenabízí FTMS."));
            service=ss.Services[0];var cs=await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellation);
            if(cs.Status!=GattCommunicationStatus.Success)throw new Exception(UiText.Get("Službu FTMS nelze načíst."));
            data=cs.Characteristics.FirstOrDefault(c=>c.Uuid==Guid.Parse("00002ad2-0000-1000-8000-00805f9b34fb"));
            control=cs.Characteristics.FirstOrDefault(c=>c.Uuid==Guid.Parse("00002ad9-0000-1000-8000-00805f9b34fb"));
            if(data==null||control==null)throw new Exception(UiText.Get("Chybí data nebo ovládání FTMS."));
            data.ValueChanged+=DataChanged;control.ValueChanged+=ControlChanged;
            if(await data.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellation)!=GattCommunicationStatus.Success||await control.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Indicate).AsTask(cancellation)!=GattCommunicationStatus.Success)throw new Exception(UiText.Get("Nepodařilo se zapnout příjem dat."));
            await Command([0],cancellation);
            cancellation.ThrowIfCancellationRequested();
            string status=UiText.Format($"Připojeno: {selected.Name}.");
            selectedTrainer=BluetoothDeviceConnection.Identify(selected,device);
            try { BluetoothDeviceConnection.Save(LastTrainerPath,selectedTrainer); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                status+=UiText.Get(" Volbu trenažéru se nepodařilo uložit.");
            }
            SetTrainerConnectionStatus(status);
        }
        catch(Exception ex)
        {
            ReleaseTrainer();
            if(!IsLoaded||(automatic&&trainerRestoreSuperseded))return;
            SetTrainerConnectionStatus(ex is OperationCanceledException?UiText.Get("Pokus o připojení skončil. Trenažér můžeš připojit ručně.")
                : UiText.Format($"Připojení trenažéru {selected.Name} selhalo: {ex.Message}"));
        }
        finally
        {
            trainerConnection=null;
            trainerConnectingAutomatically=false;
            SetTrainerActivity(null);
        }
    }
    void ConnectionChanged(BluetoothLEDevice s,object e){if(s.ConnectionStatus==BluetoothConnectionStatus.Disconnected)Dispatcher.BeginInvoke(()=>{if(ReferenceEquals(device,s)&&trainerConnection==null&&(!riding||!demo)){riding=false;timer.Stop();SetRideMode(false);string message=UiText.Get("Trenažér se odpojil. Jízda zastavena.")+EndRide("Přerušeno");DisconnectCore();SetTrainerConnectionStatus(message);}});}
    void ControlChanged(GattCharacteristic s,GattValueChangedEventArgs e){var b=new byte[e.CharacteristicValue.Length];DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(b);reply?.TrySetResult(b);}
    async Task Command(byte[] b,CancellationToken cancellation=default){
        await gate.WaitAsync(cancellation);try{
            if(control==null)throw new Exception(UiText.Get("Trenažér není připojen."));
            reply=new(TaskCreationOptions.RunContinuationsAsynchronously);using var w=new DataWriter();w.WriteBytes(b);
            if(await control.WriteValueAsync(w.DetachBuffer(),GattWriteOption.WriteWithResponse).AsTask(cancellation)!=GattCommunicationStatus.Success)throw new Exception(UiText.Get("Zápis příkazu selhal."));
            var r=await reply.Task.WaitAsync(TimeSpan.FromSeconds(5),cancellation);
            if(r.Length<3||r[0]!=0x80||r[1]!=b[0]||r[2]!=1)throw new Exception(UiText.Get("Trenažér příkaz odmítl nebo nepodporuje."));
        }finally{reply=null;gate.Release();}
    }
    void DataChanged(GattCharacteristic s,GattValueChangedEventArgs e){
        var b=new byte[e.CharacteristicValue.Length];DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(b);
        try{if(b.Length<2)return;int f=BitConverter.ToUInt16(b,0),i=2;double? v=null,c=null,p=null;
            if((f&1)==0){v=BitConverter.ToUInt16(b,i)/100.0;i+=2;}if((f&2)!=0)i+=2;
            if((f&4)!=0){c=BitConverter.ToUInt16(b,i)/2.0;i+=2;}if((f&8)!=0)i+=2;if((f&16)!=0)i+=3;if((f&32)!=0)i+=2;
            if((f&64)!=0)p=BitConverter.ToInt16(b,i);
            Dispatcher.BeginInvoke(()=>{if(v.HasValue){speed=v.Value;lastData=DateTime.UtcNow;Speed.Text=$"{speed:F1} km/h";}if(c.HasValue){Cadence.Text=UiText.Format($"{c:F0} ot/min");currentCadence=c.Value;cadenceReceived=DateTime.UtcNow;}if(p.HasValue){Power.Text=$"{p:F0} W";currentPower=p.Value;powerReceived=DateTime.UtcNow;}});
        }catch(ArgumentException){}
    }
    async void StartRide(object s,RoutedEventArgs e){
        if(route.Count<2){Status.Text=UiText.Get("Nejprve načtěte GPX.");return;}
        if(trainerConnection!=null){Status.Text=UiText.Get("Trenažér se ještě připojuje.");return;}
        if(control==null||device?.ConnectionStatus!=BluetoothConnectionStatus.Connected){Status.Text=UiText.Get("Nejprve připojte trenažér.");return;}
        if(riding||ticking||countdown!=null||starting)return;if(pendingRide!=null){Status.Text=UiText.Get("Nejprve ulož čekající jízdu.");return;}starting=true;try{if(!await PrepareCountdown(10))return;if(control==null||device?.ConnectionStatus!=BluetoothConnectionStatus.Connected){Status.Text=UiText.Get("Trenažér není připojen.");return;}await Command([7]);demo=false;BeginRide();}catch(Exception ex){Status.Text=UiText.Get("Jízdu nelze spustit: ")+ex.Message;}finally{starting=false;}
    }
    async void StartDemo(object s,RoutedEventArgs e){if(trainerConnection!=null&&!trainerConnectingAutomatically){Status.Text=UiText.Get("Trenažér se ještě připojuje.");return;}if(route.Count<2){Status.Text=UiText.Get("Nejprve načtěte GPX.");return;}if(riding||ticking||countdown!=null||starting)return;starting=true;try{if(!await PrepareCountdown(3))return;demo=true;BeginRide();}finally{starting=false;}}
    void BeginRide()
    {
        ClearProfilePreview();
        if(distance>=route[^1].D)distance=0;
        lastStreetDistance=double.NaN;
        lastGrade=double.NaN;
        lastTick=DateTime.UtcNow;
        rideStartDistance=distance;
        rideClock.Restart();
        ResetProfileTelemetry();
        session=demo?null:new RideSession(RouteName.Text, distance, CurrentRidePosition(), routeHasUnsavedEdits ? null : routeSourcePath);
        currentPower=null;
        currentCadence=null;
        powerReceived=DateTime.MinValue;
        cadenceReceived=DateTime.MinValue;
        riding=true;
        SetRideMode(true);
        UpdateView();
        timer.Start();
        PlayCountdownSound(true);
        Status.Text=demo?UiText.Get("Ukázková jízda: odpor se nemění."):UiText.Get("Jízda zahájena.");
    }
    async void Tick(object? s,EventArgs e){
        if(!riding||ticking)return;ticking=true;try{
            var now=DateTime.UtcNow;double dt=Math.Clamp((now-lastTick).TotalSeconds,0,2);lastTick=now;
            if(demo)speed=20;else if((now-lastData).TotalSeconds>5)speed=0;
            if(session!=null && currentPower.HasValue && (now-powerReceived).TotalSeconds<=5)session.AddPower(currentPower.Value,dt);
            distance=Math.Min(route[^1].D,distance+speed/3.6*dt);
            double? sampleSpeed = demo || (now-lastData).TotalSeconds<=5 ? speed : null;
            int? sampleHeartRate = CurrentHeartRate;
            AddProfileTelemetry(distance, sampleSpeed, sampleHeartRate);
            session?.AddSample(distance, sampleSpeed,
                (now-cadenceReceived).TotalSeconds<=5?currentCadence:null,
                (now-powerReceived).TotalSeconds<=5?currentPower:null,sampleHeartRate,CurrentRidePosition());
            double grade=SlopeAt(distance);UpdateView();
            if(!demo&&(double.IsNaN(lastGrade)||Math.Abs(grade-lastGrade)>=0.5)){
                short raw=(short)Math.Round(grade*100);await Command([0x11,0,0,(byte)(raw&255),(byte)((raw>>8)&255),40,51]);lastGrade=grade;
            }
            if(riding && distance>=route[^1].D){riding=false;timer.Stop();SetRideMode(false);string saved=EndRide("Dokončeno");if(!demo)await Command([8,1]);Status.Text=UiText.Get("Trasa dokončena.")+saved;}
        }catch(Exception ex){riding=false;timer.Stop();SetRideMode(false);Status.Text=UiText.Get("Jízda zastavena: ")+ex.Message+EndRide("Přerušeno");}finally{ticking=false;}
    }
    void UpdateView()
    {
        if(route.Count<2)return;
        Progress.Value=distance/route[^1].D*100;
        UpdateProfileReadout();
        if(demo||riding)Speed.Text=$"{speed:F1} km/h";
        UpdateRideMetrics();
        DrawProfile(null,null);
        SendPosition();
    }
    async void StopRide(object s,RoutedEventArgs e){if(countdown!=null){countdown.Cancel();Status.Text=UiText.Get("Odpočítávání zrušeno.");return;}bool real=riding&&!demo;riding=false;timer.Stop();SetRideMode(false);Status.Text=UiText.Get("Jízda zastavena.")+EndRide("Ukončeno");if(real)try{await Command([8,1]);}catch(Exception ex){Status.Text=UiText.Get("Jízda zastavena, trenažér nepotvrdil zastavení: ")+ex.Message;}}
    void DisconnectTrainer()
    {
        if(trainerConnection!=null){trainerConnection.Cancel();return;}
        DisconnectCore();
        SetTrainerConnectionStatus(UiText.Get("Trenažér odpojen."));
    }
    void DisconnectCore()
    {
        EndRide("Přerušeno");riding=false;timer.Stop();SetRideMode(false);
        speed=0;lastData=DateTime.MinValue;
        ReleaseTrainer();
    }
    void ReleaseTrainer()
    {
        if(data!=null)data.ValueChanged-=DataChanged;
        if(control!=null)control.ValueChanged-=ControlChanged;
        if(device!=null)device.ConnectionStatusChanged-=ConnectionChanged;
        service?.Dispose();device?.Dispose();
        service=null;device=null;data=null;control=null;
    }

    bool mapReady;
    bool navigationMode;
    string? activeGoogle3DKey;
    string? catalogFolder;
    readonly string settingsFile=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SuitoRide","catalog.txt");
    async void InitMap()
    {
        try {
            var webData=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SuitoRide","WebView2");
            var webEnvironment=await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder:webData);
            await MapView.EnsureCoreWebView2Async(webEnvironment);
            MapView.CoreWebView2.SetVirtualHostNameToFolderMapping("suitoride.local",System.IO.Path.Combine(AppContext.BaseDirectory,"map"),Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            MapView.CoreWebView2.SetVirtualHostNameToFolderMapping("suitoride-3d.local",
                System.IO.Path.Combine(AppContext.BaseDirectory,"map"),
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.DenyCors);
            MapView.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
            MapView.CoreWebView2.WebMessageReceived+=(_,e)=>{
                if(e.TryGetWebMessageAsString()=="ready"){mapReady=true;MapStatus.Text=UiText.Get("Mapa: OpenStreetMap. Poloha vychází z ujeté vzdálenosti.");SendRiderName();SendRoute();SendMap(new{type="rotate",enabled=RotateMap.IsChecked==true});SendMap(new{type="ride-layout",active=riding,fitMainMap=riding&&navigationMode});SendViewMode();foreach(var other in otherRiders.Values.ToArray())ShowOtherRider(other);UpdateStreetView(true);}
            };
            MapView.CoreWebView2.NavigationCompleted+=(_,e)=>{if(!e.IsSuccess)MapStatus.Text=UiText.Get("Mapu se nepodařilo načíst. Zkontrolujte připojení a WebView2 Runtime.");};
            MapView.CoreWebView2.NewWindowRequested+=(_,e)=>{e.Handled=true;try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri){UseShellExecute=true});}catch{}};
            MapView.Source=new Uri($"https://suitoride.local/index.html?lang={UiText.Language}");
        }catch(Exception ex){MapStatus.Text=UiText.Get("Mapu nelze spustit: ")+ex.Message;}
    }
    void SendMap(object message){if(mapReady) MapView.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(message));}
    void SendRoute(){if(route.Count<2)return;SendMap(new{type="route",points=route.Select(p=>new[]{p.Lat,p.Lon})});SendPosition();}
    RidePosition CurrentRidePosition()
    {
        var (latitude, longitude) = PositionAt(distance);
        return new(latitude, longitude, Elev(distance));
    }
    void SendDisplayedPosition()
    {
        if(route.Count<2)return;
        double meters = DisplayDistance;
        var point = PositionAt(meters);
        SendMap(new{type="position",lat=point.Lat,lon=point.Lon,heading=HeadingAt(meters),altitude=Elev(meters),distance=meters,animate=riding&&distance>rideStartDistance});
    }
    void SendPosition()
    {
        SendDisplayedPosition();
        sharedRide?.Publish(distance,riding);
        UpdateStreetView();
    }
    void FitMap(object s,RoutedEventArgs e)=>SendMap(new{type="fit"});
    void FollowRider(object s,RoutedEventArgs e)=>SendMap(new{type="follow"});
    void SendViewMode()
    {
        if(activeGoogle3DKey!=null)
            SendMap(new{type="google-3d-mode",key=activeGoogle3DKey});
        else
            SendMap(new{type="navigation-mode",enabled=navigationMode});
    }
    void ShowNavigation(object s,RoutedEventArgs e)
    {
        activeGoogle3DKey=null;
        navigationMode=true;
        SendViewMode();
    }
    void ShowPanorama(object s,RoutedEventArgs e)
    {
        activeGoogle3DKey=null;
        navigationMode=false;
        SendViewMode();
        UpdateStreetView(true);
    }
    void ShowGoogle3D(object sender,RoutedEventArgs e)
    {
        try
        {
            var key=PanoramaKeyStore.Load("google3d");
            if(string.IsNullOrWhiteSpace(key))
            {
                OpenSettings("google3d");
                key=PanoramaKeyStore.Load("google3d");
                if(string.IsNullOrWhiteSpace(key))return;
            }
            if(activeGoogle3DKey==null)RotateMap.IsChecked=false;
            activeGoogle3DKey=key;
            navigationMode=true;
            SendViewMode();
        }
        catch(Exception ex){Status.Text=UiText.Get("Google 3D nelze zapnout: ")+ex.Message;}
    }
    void RestoreCatalog()
    {
        try{if(File.Exists(settingsFile)){catalogFolder=File.ReadAllText(settingsFile).Trim();RefreshCatalogCore();}}
        catch(Exception ex){CatalogInfo.Text=UiText.Get("Katalog nelze načíst: ")+ex.Message;}
    }
    void ChooseFolder(object s,RoutedEventArgs e)
    {
        var dlg=new OpenFolderDialog{Title=UiText.Get("Vyberte složku s GPX trasami"),Multiselect=false};
        if(dlg.ShowDialog()!=true)return;
        catalogFolder=dlg.FolderName;
        try{Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsFile)!);File.WriteAllText(settingsFile,catalogFolder);}
        catch(Exception ex){CatalogInfo.Text=UiText.Get("Složku nelze uložit: ")+ex.Message;return;}
        RefreshCatalogCore();
    }
    void RefreshCatalog(object s,RoutedEventArgs e)=>RefreshCatalogCore();
    void RefreshCatalogCore()
    {
        if(string.IsNullOrWhiteSpace(catalogFolder)){CatalogInfo.Text=UiText.Get("Nejprve vyberte složku tras.");return;}
        try{
            var files=new DirectoryInfo(catalogFolder).EnumerateFiles().Where(f=>string.Equals(f.Extension,".gpx",StringComparison.OrdinalIgnoreCase)).OrderBy(f=>f.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
            SetCatalogEntries(files,LoadCompletedRideCounts());
            CatalogInfo.Text=UiText.Format($"Katalog: {catalogFolder} · {files.Count} tras");
        }catch(Exception ex){Catalog.ItemsSource=null;CatalogInfo.Text=UiText.Get("Katalog nelze načíst: ")+ex.Message;}
    }
    sealed record CatalogEntry(FileInfo File, int? Completed)
    {
        public string CountLabel => Completed is int count ? $"{count}×" : "—";
    }
    bool updatingCatalogSelection;

    void SetCatalogEntries(IEnumerable<FileInfo> files, CompletedRideCounts? counts)
    {
        var entries=files.Select(file=>new CatalogEntry(file,
            counts?.ForRoute(file.FullName,System.IO.Path.GetFileNameWithoutExtension(file.Name)))).ToList();
        updatingCatalogSelection=true;
        try { Catalog.ItemsSource=entries; }
        finally { updatingCatalogSelection=false; }
        SyncCatalogSelection();
    }

    void SyncCatalogSelection()
    {
        var selectedPath=routeHasUnsavedEdits ? null : routeSourcePath;
        var entries=Catalog.ItemsSource as IEnumerable<CatalogEntry>;
        var selectedEntry=entries?.FirstOrDefault(entry=>
            string.Equals(entry.File.FullName,selectedPath,StringComparison.OrdinalIgnoreCase));
        updatingCatalogSelection=true;
        try { Catalog.SelectedItem=selectedEntry; }
        finally { updatingCatalogSelection=false; }
    }

    void CatalogSelectionChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(updatingCatalogSelection||Catalog.SelectedItem is not CatalogEntry entry)return;
        try
        {
            if(riding||ticking||starting){Status.Text=UiText.Get("Nejprve zastavte jízdu.");return;}
            LoadRouteFile(entry.File.FullName);
        }
        finally { SyncCatalogSelection(); }
    }

    int panoramaSpacing=20;
    string PanoramaSpacingFile => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsFile)!,"panorama-spacing.txt");

    void RestorePanoramaSpacing()
    {
        try
        {
            if(File.Exists(PanoramaSpacingFile) &&
                int.TryParse(File.ReadAllText(PanoramaSpacingFile),out int spacing) &&
                MapSettingsWindow.SpacingOptions.Contains(spacing))
                panoramaSpacing=spacing;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            StreetStatus.Text=UiText.Get("Rozestup panoramat se nepodařilo načíst.");
        }
    }
    void SavePanoramaSpacing(int spacing)
    {
        if(spacing==panoramaSpacing)return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PanoramaSpacingFile)!);
        File.WriteAllText(PanoramaSpacingFile,spacing.ToString(CultureInfo.InvariantCulture));
        panoramaSpacing=spacing;
        if(streetKey!=null && route.Count>=2)ShowPanoramaSpacing();
    }
    string riderName="";
    string RiderNamePath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsFile)!,"rider-name.txt");

    void RestoreRiderName()
    {
        try
        {
            if(File.Exists(RiderNamePath))
                riderName=File.ReadAllText(RiderNamePath).Trim();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
    }

    void SaveRiderName(string name)
    {
        if(name.Length==0)File.Delete(RiderNamePath);
        else
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RiderNamePath)!);
            File.WriteAllText(RiderNamePath,name);
        }
        riderName=name;
        SendRiderName();
    }

    void SendRiderName()=>SendMap(new{type="rider-name",name=riderName});

    void ShowMapSettings(object sender,RoutedEventArgs e) => OpenSettings();

    void OpenSettings(string? keyProvider=null)
    {
        new MapSettingsWindow(MapView.CoreWebView2?.Profile,panoramaSpacing,SavePanoramaSpacing,riderName,SaveRiderName,
            keyProvider ?? (streetKey!=null ? streetProvider : null)) {Owner=this}.ShowDialog();
        try
        {
            if(activeGoogle3DKey!=null)
            {
                string? key=PanoramaKeyStore.Load("google3d");
                if(key!=activeGoogle3DKey)
                {
                    activeGoogle3DKey=string.IsNullOrWhiteSpace(key) ? null : key;
                    SendViewMode();
                }
            }
            if(streetKey!=null)
            {
                string? key=PanoramaKeyStore.Load(streetProvider);
                if(string.IsNullOrWhiteSpace(key)){TurnOffPanorama();RememberPanoramaProvider("off");}
                else if(key!=streetKey)
                {
                    SetActivePanorama(streetProvider,key);
                    UpdateStreetView(true);
                }
            }
        }
        catch(Exception ex){Status.Text=UiText.Get("Klíč se nepodařilo uložit nebo načíst: ")+ex.Message;}
    }
    void ShowPanoramaSpacing() =>
        StreetStatus.Text=UiText.Format($"Panorama zapnuto. Obnovuje se po {panoramaSpacing} m. Dostupnost snímků závisí na trase.");

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
    double HeadingAt(double meters)
    {
        if(route.Count<2)return 0;
        int next=route.FindIndex(p=>p.D>meters+1e-8);
        if(next<1)next=route.Count-1;
        var a=route[next-1];var b=route[next];
        double rad=Math.PI/180,lat1=a.Lat*rad,lat2=b.Lat*rad,dlon=(b.Lon-a.Lon)*rad;
        double y=Math.Sin(dlon)*Math.Cos(lat2);
        double x=Math.Cos(lat1)*Math.Sin(lat2)-Math.Sin(lat1)*Math.Cos(lat2)*Math.Cos(dlon);
        return (Math.Atan2(y,x)/rad+360)%360;
    }
    void SetMapRotation(object s,RoutedEventArgs e)=>SendMap(new{type="rotate",enabled=RotateMap.IsChecked==true});
    bool updatingPanoramaPicker;

    void SetPanoramaPicker(string provider)
    {
        updatingPanoramaPicker=true;
        try { PanoramaProvider.SelectedValue=provider; }
        finally { updatingPanoramaPicker=false; }
    }

    void PanoramaProviderChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(!IsLoaded || updatingPanoramaPicker || PanoramaProvider.SelectedValue is not string provider)return;
        if(provider=="off")
        {
            TurnOffPanorama();
            RememberPanoramaProvider(provider);
            return;
        }
        try
        {
            string? key=PanoramaKeyStore.Load(provider);
            if(string.IsNullOrWhiteSpace(key))
            {
                PanoramaProvider.IsDropDownOpen=false;
                SetPanoramaPicker(streetKey==null ? "off" : streetProvider);
                OpenSettings(provider);
                key=PanoramaKeyStore.Load(provider);
                if(string.IsNullOrWhiteSpace(key))return;
            }
            SetActivePanorama(provider,key);
            RememberPanoramaProvider(provider);
            ShowPanorama(sender,e);
        }
        catch(Exception ex)
        {
            SetPanoramaPicker(streetKey==null ? "off" : streetProvider);
            Status.Text=UiText.Get("Klíč se nepodařilo uložit nebo načíst: ")+ex.Message;
        }
    }

    void SetActivePanorama(string provider,string key)
    {
        streetProvider=provider;
        streetKey=key;
        lastStreetDistance=double.NaN;
        SetPanoramaPicker(provider);
        if(route.Count>=2)ShowPanoramaSpacing();
        else StreetStatus.Text=UiText.Get("Panorama zapnuto. Pro zobrazení načtěte GPX trasu.");
    }

    void TurnOffPanorama()
    {
        streetKey=null;
        lastStreetDistance=double.NaN;
        SetPanoramaPicker("off");
        SendMap(new{type="streetview-off"});
        StreetStatus.Text=UiText.Get("Street View je vypnuté.");
    }

    void RememberPanoramaProvider(string provider)
    {
        try { PanoramaKeyStore.RememberProvider(provider); }
        catch { StreetStatus.Text+=UiText.Get(" Poskytovatele se nepodařilo uložit pro příští spuštění."); }
    }
    void UpdateStreetView(bool force=false)
    {
        if(!mapReady||streetKey==null||route.Count<2||navigationMode||activeGoogle3DKey!=null)return;
        double meters = DisplayDistance;
        if(!force&&(!riding||(!double.IsNaN(lastStreetDistance)&&Math.Abs(meters-lastStreetDistance)<panoramaSpacing)))return;
        var p=PositionAt(meters);
        SendMap(new{type="streetview",provider=streetProvider,key=streetKey,lat=p.Lat,lon=p.Lon,heading=HeadingAt(meters)});
        lastStreetDistance=meters;
    }

    void AutoStartPanorama()
    {
        try
        {
            var provider=PanoramaKeyStore.LastProvider();
            if(provider=="off")TurnOffPanorama();
            else if(provider!=null && PanoramaKeyStore.Load(provider) is string key && !string.IsNullOrWhiteSpace(key))
            {
                SetActivePanorama(provider,key);
                UpdateStreetView(true);
            }
            else StreetStatus.Text=UiText.Get("Pro automatické zapnutí zadejte a uložte API klíč.");
        }
        catch(Exception ex) { StreetStatus.Text=UiText.Get("Panorama se nepodařilo automaticky zapnout: ")+ex.Message; }
    }

    void SetRideMode(bool active)
    {
        if(!active)rideClock.Stop();
        SetFullscreen(active);
        SetupPanel.Visibility=active?Visibility.Collapsed:Visibility.Visible;
        TrainerButton.Visibility=HeartRateButton.Visibility=SetupPanel.Visibility;
        bool fitMainMap=active && navigationMode;
        if(fitMainMap)RotateMap.IsChecked=false;
        SendMap(new{type="ride-layout",active,fitMainMap});
        sharedRide?.Publish(distance,active);
        StartButton.Visibility=active?Visibility.Collapsed:Visibility.Visible;
        DemoButton.Visibility=active?Visibility.Collapsed:Visibility.Visible;
        StopButton.Visibility=active?Visibility.Visible:Visibility.Collapsed;
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Auto)] struct MonitorInfo { public int Size; public NativeRect Monitor,Work; public int Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr hwnd,IntPtr insertAfter,int x,int y,int width,int height,uint flags);
    static void PlaceOnOwnerMonitor(Window target,Window owner)
    {
        var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
        if(GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(owner).Handle,2),ref info))
        {
            var r=info.Monitor;
            SetWindowPos(new WindowInteropHelper(target).Handle,IntPtr.Zero,r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top,0x0004|0x0010);
        }
    }
    static Rect? GetMonitorBounds(Window window)
    {
        var source=PresentationSource.FromVisual(window);
        var monitor=MonitorFromWindow(new WindowInteropHelper(window).Handle,2);
        var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
        if(source?.CompositionTarget==null||!GetMonitorInfo(monitor,ref info))return null;
        var transform=source.CompositionTarget.TransformFromDevice;
        var topLeft=transform.Transform(new Point(info.Monitor.Left,info.Monitor.Top));
        var bottomRight=transform.Transform(new Point(info.Monitor.Right,info.Monitor.Bottom));
        return new Rect(topLeft,bottomRight);
    }
    bool fullscreen;
    WindowStyle savedStyle;
    WindowState savedState;
    bool savedTopmost;
    double savedLeft,savedTop,savedWidth,savedHeight;
    void SetFullscreen(bool active)
    {
        if(active==fullscreen)return;
        if(active)
        {
            savedStyle=WindowStyle;savedState=WindowState;savedTopmost=Topmost;
            var bounds=RestoreBounds;
            savedLeft=bounds.Left;savedTop=bounds.Top;savedWidth=bounds.Width;savedHeight=bounds.Height;
            var boundsOnMonitor=GetMonitorBounds(this);
            if(!boundsOnMonitor.HasValue)return;
            WindowState=WindowState.Normal;WindowStyle=WindowStyle.None;
            Left=boundsOnMonitor.Value.Left;Top=boundsOnMonitor.Value.Top;Width=boundsOnMonitor.Value.Width;Height=boundsOnMonitor.Value.Height;
            Topmost=true;fullscreen=true;
        }
        else
        {
            Topmost=savedTopmost;WindowStyle=savedStyle;
            Left=savedLeft;Top=savedTop;Width=savedWidth;Height=savedHeight;
            WindowState=savedState;fullscreen=false;
        }
    }
    readonly RideHistoryStore historyStore=new();
    RideSession? session;
    RideRecord? pendingRide;
    double? currentPower, currentCadence;
    DateTime cadenceReceived=DateTime.MinValue;
    DateTime powerReceived=DateTime.MinValue;
    string EndRide(string result)
    {
        if(session==null)return "";
        var record=session.Finish(distance, result, CurrentRidePosition());
        session=null;
        if(record.DistanceMeters<0.1)return "";
        pendingRide=record;
        try {
            historyStore.Save(record); pendingRide=null; RetrySaveButton.Visibility=Visibility.Collapsed;RefreshCompletedRideCounts();
            return UiText.Get(" Jízda uložena do historie.");
        } catch(Exception) {
            RetrySaveButton.Visibility=Visibility.Visible;
            return UiText.Get(" Jízdu se nepodařilo uložit. Použij tlačítko Uložit čekající jízdu.");
        }
    }
    void RetrySaveRide(object s,RoutedEventArgs e)
    {
        if(pendingRide==null)return;
        try { historyStore.Save(pendingRide);pendingRide=null;RetrySaveButton.Visibility=Visibility.Collapsed;RefreshCompletedRideCounts();Status.Text=UiText.Get("Jízda uložena do historie."); }
        catch(Exception ex) { Status.Text=UiText.Get("Jízdu nelze uložit: ")+ex.Message; }
    }
    void ShowHistory(object s,RoutedEventArgs e) => new HistoryWindow(historyStore) { Owner=this }.ShowDialog();
    void HandleClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if(installingUpdate){e.Cancel=true;return;}
        if(riding)
        {
            e.Cancel = true;
            Status.Text = UiText.Get("Před zavřením aplikace zastav jízdu.");
            return;
        }
        countdown?.Cancel();
        EndRide("Přerušeno");
        if(pendingRide!=null) {
            riding=false;timer.Stop();SetRideMode(false);
            e.Cancel=true;
            Status.Text=UiText.Get("Jízdu se nepodařilo uložit. Zkontroluj přístup k datům a zkus uložení znovu.");
            MessageBox.Show(Status.Text,UiText.Get("Neuložená jízda"),MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }

    string? routeSourcePath;
    bool routeHasUnsavedEdits;
    string LastRouteFile => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsFile)!,"last-route.txt");
    void RestoreLastRoute()
    {
        try
        {
            if(!File.Exists(LastRouteFile))return;
            var path=File.ReadAllText(LastRouteFile);
            if(File.Exists(path))LoadRouteFile(path,restoring:true);
            else Status.Text=UiText.Get("Poslední GPX soubor už není dostupný.");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            Status.Text=UiText.Get("Poslední trasu se nepodařilo obnovit: ")+ex.Message;
        }
    }
    void RememberLastRoute(string path)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LastRouteFile)!);
            File.WriteAllText(LastRouteFile,path);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            Status.Text+=UiText.Get(" Cestu k poslední trase se nepodařilo uložit.");
        }
    }
    sealed class CompletedRideCounts
    {
        readonly Dictionary<string,int> byPath=new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,int> byName=new(StringComparer.OrdinalIgnoreCase);

        public CompletedRideCounts(IEnumerable<RideRecord> rides)
        {
            foreach(var ride in rides.Where(ride=>ride.Result=="Dokončeno"))
            {
                var counts=ride.RoutePath==null ? byName : byPath;
                string key=ride.RoutePath ?? ride.RouteName;
                counts[key]=counts.GetValueOrDefault(key)+1;
            }
        }
        public int ForRoute(string? path,string name) =>
            (path==null ? 0 : byPath.GetValueOrDefault(path))+byName.GetValueOrDefault(name);
    }
    CompletedRideCounts? LoadCompletedRideCounts()
    {
        try{return new CompletedRideCounts(historyStore.Load().Rides);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return null;}
    }
    void RefreshCompletedRideCounts()
    {
        var counts=LoadCompletedRideCounts();
        UpdateRouteInfo(counts);
        if(Catalog.ItemsSource is IEnumerable<CatalogEntry> entries)
            SetCatalogEntries(entries.Select(entry=>entry.File),counts);
    }
    void UpdateRouteInfo()=>UpdateRouteInfo(LoadCompletedRideCounts());
    void UpdateRouteInfo(CompletedRideCounts? counts)
    {
        if(route.Count<2)return;
        var completed=counts?.ForRoute(routeHasUnsavedEdits ? null : routeSourcePath,RouteName.Text);
        string count=completed is int total ? UiText.Format($"Dokončeno: {total}×") : UiText.Get("Historie nedostupná");
        double ascent=0, descent=0;
        for(int i=1;i<route.Count;i++)
        {
            double change=route[i].Ele-route[i-1].Ele;
            if(change>0)ascent+=change;
            else descent-=change;
        }
        RouteInfo.Text=UiText.Format($"{count} · {route[^1].D/1000:F1} km · +{ascent:F0} / −{descent:F0} m · {route.Count} bodů · výška {route.Min(p=>p.Ele):F0}–{route.Max(p=>p.Ele):F0} m");
    }
    List<Pt> CreateEditedRoute(double start, double end, bool reverse)
    {
        if(route.Count<2 || start<0 || end>route[^1].D || end-start<10)throw new ArgumentException(UiText.Get("Neplatný úsek trasy."));
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
        if(result.Count<2 || length<1)throw new InvalidDataException(UiText.Get("Vybraný úsek neobsahuje dostatek různých bodů."));
        return result;
    }
    void EditRoute(object s,RoutedEventArgs e)
    {
        if(riding||ticking){Status.Text=UiText.Get("Nejprve zastavte jízdu.");return;}
        if(route.Count<2){Status.Text=UiText.Get("Nejprve načtěte GPX trasu.");return;}
        if(SharedRouteLocked())return;
        if(elevationProfile==null)return;
        var dialog=new RouteEditWindow(route[^1].D,elevationProfile){Owner=this};
        if(dialog.ShowDialog()!=true)return;
        try {
            var edited=CreateEditedRoute(dialog.StartMeters,dialog.EndMeters,dialog.Reverse);
            SetRoute(edited);distance=0;lastStreetDistance=double.NaN;
            RouteName.Text=RouteName.Text+UiText.Get(" – upravená");
            routeHasUnsavedEdits=true;
            SyncCatalogSelection();
            UpdateRouteInfo();
            UpdateView();SendRoute();UpdateStreetView(true);
            Status.Text=UiText.Get("Upravená trasa je připravena k jízdě.");
            if(MessageBox.Show(this,UiText.Get("Chceš upravenou trasu uložit jako nový GPX soubor?"),UiText.Get("Upravit trasu"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            var save=new SaveFileDialog{Filter="GPX trasa (*.gpx)|*.gpx",DefaultExt=".gpx",AddExtension=true,FileName=RouteName.Text+".gpx",OverwritePrompt=true};
            if(save.ShowDialog(this)!=true){Status.Text=UiText.Get("Trasa je upravená pro tuto jízdu; GPX nebyl uložen.");return;}
            if(routeSourcePath!=null && string.Equals(System.IO.Path.GetFullPath(save.FileName),System.IO.Path.GetFullPath(routeSourcePath),StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this,UiText.Get("Vyber jiný soubor, aby původní GPX zůstal zachován."),UiText.Get("Uložit trasu"));Status.Text=UiText.Get("GPX nebyl uložen.");return; }
            SaveEditedGpx(save.FileName);
            routeSourcePath=System.IO.Path.GetFullPath(save.FileName);
            routeHasUnsavedEdits=false;
            RouteName.Text=System.IO.Path.GetFileNameWithoutExtension(routeSourcePath);
            UpdateRouteInfo();
            Status.Text=UiText.Get("Upravená trasa byla uložena jako nový GPX.");
            RememberLastRoute(routeSourcePath);
            if(!string.IsNullOrEmpty(catalogFolder))RefreshCatalogCore();
        } catch(Exception ex) { MessageBox.Show(this,UiText.Get("Úpravu nebo uložení trasy se nepodařilo dokončit: ")+ex.Message,UiText.Get("Upravit trasu")); }
    }

    CancellationTokenSource? countdown;
    bool starting;
    async Task<bool> PrepareCountdown(int seconds)
    {
        if(countdown!=null)return false;
        var cancel=new CancellationTokenSource();countdown=cancel;
        StartButton.IsEnabled=false;DemoButton.IsEnabled=false;SetupPanel.IsEnabled=false;
        StopButton.Visibility=Visibility.Visible;
        CountdownWindow? overlay=null;
        try {
            overlay=new CountdownWindow(seconds,()=>{if(!cancel.IsCancellationRequested){Status.Text=UiText.Get("Odpočítávání zrušeno.");cancel.Cancel();}});
            if(IsVisible){overlay.Owner=this;var bounds=GetMonitorBounds(this);if(bounds.HasValue)overlay.PlaceOnMonitor(bounds.Value);}
            overlay.Show();
            if(IsVisible)PlaceOnOwnerMonitor(overlay,this);
            for(int remaining=seconds;remaining>0;remaining--) {
                Status.Text=UiText.Format($"Jízda začne za {remaining} s…");
                overlay.SetRemaining(remaining);
                if(remaining<=3)PlayCountdownSound(false);
                await Task.Delay(1000,cancel.Token);
            }
            cancel.Token.ThrowIfCancellationRequested();
            return true;
        } catch(OperationCanceledException) { return false; }
        catch(Exception ex) { Status.Text=UiText.Get("Odpočet se nepodařilo spustit: ")+ex.Message;return false; }
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
        if(route.Count<2){Status.Text=UiText.Get("Nejprve načti GPX trasu pro společnou jízdu.");return;}
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
        Status.Text=UiText.Get("Pro změnu trasy se nejprve odpoj ze společné jízdy.");
        return true;
    }
    static void PlayCountdownSound(bool start)
    {
        try { if(start)System.Media.SystemSounds.Asterisk.Play();else System.Media.SystemSounds.Beep.Play(); }
        catch(Exception) { /* Odpočet funguje i bez dostupného zvukového zařízení. */ }
    }}
