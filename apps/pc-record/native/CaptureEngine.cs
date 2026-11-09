using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorderLib;
using Microsoft.Win32;
namespace PCRecord {
 // Движок управляет состояниями записи; вызовы интерфейса возвращаются в поток окна.
 internal sealed class CaptureEngine:IDisposable {
  readonly Paths paths;readonly MediaTools media;readonly Action<Action> dispatch;readonly Func<long> freeBytes;readonly System.Windows.Forms.Timer safetyTimer=new System.Windows.Forms.Timer{Interval=2000};
  CancellationTokenSource pending;DateTime countdownEnd;string warning="";IntPtr sourceWindow;DateTime lastSafetyUtc;bool disposed;
  [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool IsWindow(IntPtr window);
  Recorder recorder;TaskCompletionSource<Dictionary<string,object>> completion;
  readonly Stopwatch clock=new Stopwatch();string state="Idle",error="",lastFile="",target="",nativeTarget="";bool snapshot;int frames;
  public CaptureEngine(Paths p,MediaTools m,Action<Action> d,Func<long> availableBytes=null){paths=p;media=m;dispatch=d;freeBytes=availableBytes??p.FreeBytes;safetyTimer.Tick+=CheckSpace;SystemEvents.PowerModeChanged+=OnPowerModeChanged;}
  // Слушаем события питания; настройки Windows, сна и тепловой защиты не изменяются.
  void OnPowerModeChanged(object sender,PowerModeChangedEventArgs e){if(disposed||e.Mode!=PowerModes.Suspend)return;try{dispatch(()=>StopForInterruption("Windows приостанавливает работу. Запись остановлена с сохранением доступных данных."));}catch(InvalidOperationException){}}
  async void StopForInterruption(string message){if(!IsVideoRecording||state=="Stopping"||state=="Converting")return;warning=message;try{await Stop();}catch(Exception ex){Failed("Не удалось завершить запись после приостановки: "+ex.Message);}}
  public bool ShowToolbar{get;private set;}
  // Проверяем остаток места без заполнения диска и сохраняем уже записанный материал.
  async void CheckSpace(object sender,EventArgs e){if(recorder==null||snapshot||state=="Stopping")return;try{DateTime now=DateTime.UtcNow;if(lastSafetyUtc!=default(DateTime)&&(now-lastSafetyUtc).TotalSeconds>30){lastSafetyUtc=now;StopForInterruption("Обнаружен длительный разрыв времени в работе приложения. Запись остановлена; начните новую после проверки файла.");return;}lastSafetyUtc=now;if(sourceWindow!=IntPtr.Zero&&!IsWindow(sourceWindow)){warning="Окно источника закрыто. Запись остановлена и сохранена.";await Stop();return;}if(freeBytes()<64L*1024*1024){warning="Запись остановлена: на D осталось менее 64 МБ.";await Stop();}}catch(Exception ex){Failed("Не удалось безопасно завершить запись: "+ex.Message);}}
  public bool IsVideoRecording{get{return !snapshot&&Busy;}}
  public bool Busy{get{return pending!=null||recorder!=null||state=="Converting";}}
  public Dictionary<string,object> Status(){return Data.Map("warning",warning,"countdown",state=="Countdown"?Math.Max(0,(int)Math.Ceiling((countdownEnd-DateTime.UtcNow).TotalSeconds)):0,"operation",snapshot?"snapshot":"video","state",state,"error",error,"elapsed",(int)clock.Elapsed.TotalSeconds,"frames",frames,"file",lastFile,"freeBytes",freeBytes(),"folder",paths.Records);}
  public static object[] Sources(string kind){
   if(kind=="window")return Recorder.GetWindows().Where(w=>w.IsValidWindow()&&!w.IsMinmimized()&&!String.IsNullOrWhiteSpace(w.Title)).Select(w=>(object)Data.Map("id",w.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),"name",w.Title)).Take(200).ToArray();
   if(kind=="camera")return Recorder.GetSystemVideoCaptureDevices().Select(c=>(object)Data.Map("id",c.DeviceName,"name",c.FriendlyName)).ToArray();
   if(kind=="display"||kind=="area")return Recorder.GetDisplays().Select(d=>(object)Data.Map("id",d.DeviceName,"name",d.FriendlyName)).ToArray();
   throw new ArgumentException("Неизвестный тип источника.");
  }
  public static object Devices(){return Data.Map("loopback",Recorder.GetSystemAudioLoopbackDevices().Select(d=>Data.Map("id",d.DeviceName,"name",d.FriendlyName,"isDefault",d.IsDefaultDevice)).ToArray(),"microphones",Recorder.GetSystemAudioCaptureDevices().Select(d=>Data.Map("id",d.DeviceName,"name",d.FriendlyName,"isDefault",d.IsDefaultDevice)).ToArray(),"cameras",Recorder.GetSystemVideoCaptureDevices().Select(d=>Data.Map("id",d.DeviceName,"name",d.FriendlyName)).ToArray());}
  RecorderOptions Options(Dictionary<string,object> args,bool isSnapshot){
   string kind=Data.Text(args,"kind"),id=Data.Text(args,"source");RecordingSourceBase source;
   if(kind=="window"){
    var win=Recorder.GetWindows().FirstOrDefault(w=>w.Handle.ToInt64().ToString(CultureInfo.InvariantCulture)==id&&w.IsValidWindow()&&!w.IsMinmimized());
    if(win==null)throw new IOException("Окно закрыто, свёрнуто или больше недоступно.");source=new WindowRecordingSource(win.Handle){IsBorderRequired=Convert.ToBoolean(Data.Get(args,"border",true)),IsCursorCaptureEnabled=Convert.ToBoolean(Data.Get(args,"cursor",true))};
   } else if(kind=="camera") {
    if(!Recorder.GetSystemVideoCaptureDevices().Any(c=>c.DeviceName==id))throw new IOException("Камера недоступна.");source=new VideoCaptureRecordingSource(id);
   } else {
    if(kind!="display"&&kind!="area")throw new IOException("Выберите источник записи.");
    if(!Recorder.GetDisplays().Any(d=>d.DeviceName==id))throw new IOException("Монитор недоступен.");
    source=new DisplayRecordingSource(id){RecorderApi=RecorderApi.WindowsGraphicsCapture,IsBorderRequired=Convert.ToBoolean(Data.Get(args,"border",true)),IsCursorCaptureEnabled=Convert.ToBoolean(Data.Get(args,"cursor",true))};
    if(kind=="area"){
     var screen=System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==id);
     int x=Data.Num(args,"x"),y=Data.Num(args,"y"),w=Data.Num(args,"width"),h=Data.Num(args,"height");
     if(screen==null||x<0||y<0||w<16||h<16||x+w>screen.Bounds.Width||y+h>screen.Bounds.Height)throw new IOException("Область не помещается на выбранном мониторе.");source.SourceRect=new ScreenRect(x,y,w,h);
    }
   }
   var audio=new List<AudioSourceBase>();
   if(!isSnapshot&&Data.Flag(args,"systemAudio")){
    string device=Data.Text(args,"loopback");if(!Recorder.GetSystemAudioLoopbackDevices().Any(d=>d.DeviceName==device))throw new IOException("Устройство системного звука недоступно.");audio.Add(new LoopbackAudioSource(device){Volume=Volume(args,"systemVolume"),IsAudioCaptureEnabled=true});
   }
   if(!isSnapshot&&Data.Flag(args,"microphone")){
    string device=Data.Text(args,"micDevice");if(!Recorder.GetSystemAudioCaptureDevices().Any(d=>d.DeviceName==device))throw new IOException("Выбранный микрофон недоступен.");audio.Add(new CaptureAudioSource(device){Volume=Volume(args,"micVolume"),IsAudioCaptureEnabled=true});
   }
   var overlays=new List<RecordingOverlayBase>();
   if(!isSnapshot&&kind!="camera"&&Data.Flag(args,"camera")){
    string camera=Data.Text(args,"cameraDevice");if(!Recorder.GetSystemVideoCaptureDevices().Any(c=>c.DeviceName==camera))throw new IOException("Выбранная камера недоступна.");overlays.Add(new VideoCaptureOverlay(camera){Size=new ScreenSize(240,180),Offset=new ScreenSize(18,18),AnchorPoint=Anchor.BottomRight});
   }
   int fps=Data.Num(args,"fps",30);if(fps!=30&&fps!=60)throw new IOException("Поддерживаются режимы 30 и 60 FPS.");
   var output=new OutputOptions{RecorderMode=isSnapshot?RecorderMode.Screenshot:RecorderMode.Video,IsVideoCaptureEnabled=true};
   string resolution=Data.Text(args,"resolution","source");if(resolution=="1080")output.OutputFrameSize=new ScreenSize(1920,1080);else if(resolution=="720")output.OutputFrameSize=new ScreenSize(1280,720);else if(resolution!="source")throw new IOException("Неизвестное разрешение.");
   return new RecorderOptions{
    SourceOptions=new SourceOptions{RecordingSources=new List<RecordingSourceBase>{source}},
    OutputOptions=output,
    AudioOptions=new AudioOptions{IsAudioEnabled=audio.Count>0,AudioSources=audio},
    OverlayOptions=new OverLayOptions{Overlays=overlays},
    VideoEncoderOptions=new VideoEncoderOptions{Framerate=fps,IsFixedFramerate=true,Bitrate=8000000,Quality=75,IsHardwareEncodingEnabled=false,IsMp4FastStartEnabled=false,IsFragmentedMp4Enabled=true},
    MouseOptions=new MouseOptions{IsMousePointerEnabled=Convert.ToBoolean(Data.Get(args,"cursor",true)),IsMouseClicksDetected=Data.Flag(args,"clicks"),MouseLeftClickDetectionColor="#FA7683",MouseRightClickDetectionColor="#8EB7FF",MouseClickDetectionDuration=250,MouseClickDetectionRadius=18},
    SnapshotOptions=new SnapshotOptions{SnapshotsWithVideo=false,SnapshotsDirectory=paths.Records,SnapshotFormat=ScreenRecorderLib.ImageFormat.PNG}
   };
  }
  static float Volume(Dictionary<string,object>a,string key){int n=Data.Num(a,key,80);if(n<0||n>100)throw new IOException("Громкость должна быть от 0 до 100.");return n/100f;}
  public Task<Dictionary<string,object>> Start(Dictionary<string,object> args,bool isSnapshot,bool preview=false){
   if(Busy)throw new IOException("Дождитесь завершения текущей операции.");if(freeBytes()<256L*1024*1024)throw new IOException("На D недостаточно свободного места: нужно не менее 256 МБ.");
   string format=Data.Text(args,"format","MP4");if(format!="MP4"&&format!="MKV")throw new IOException("Неизвестный формат.");
   int delay=isSnapshot?0:Data.Num(args,"countdown",0);if(delay!=0&&delay!=3&&delay!=5)throw new IOException("Отсчёт поддерживает 0, 3 или 5 секунд.");
   var options=Options(args,isSnapshot);sourceWindow=Data.Text(args,"kind")=="window"?new IntPtr(Int64.Parse(Data.Text(args,"source"),CultureInfo.InvariantCulture)):IntPtr.Zero;warning="";ShowToolbar=Convert.ToBoolean(Data.Get(args,"toolbar",true));error="";frames=0;lastFile="";snapshot=isSnapshot;clock.Reset();state="Starting";
   completion=new TaskCompletionSource<Dictionary<string,object>>();
   target=preview?Path.Combine(paths.Preview,"preview-"+Guid.NewGuid().ToString("N")+".png"):paths.NewRecording(isSnapshot?".png":format=="MKV"?".mkv":".mp4");
   nativeTarget=(!isSnapshot&&format=="MKV")?Path.Combine(paths.Temp,"record-"+Guid.NewGuid().ToString("N")+".mp4"):target;
   if(delay>0){pending=new CancellationTokenSource();state="Countdown";countdownEnd=DateTime.UtcNow.AddSeconds(delay);BeginAfterDelay(options,delay,pending.Token);}else BeginRecording(options);
   return completion.Task;
  }
  // Отсчёт происходит до открытия источников аудио и камеры; отмена ничего не записывает.
  async void BeginAfterDelay(RecorderOptions options,int seconds,CancellationToken token){try{await Task.Delay(seconds*1000,token);if(token.IsCancellationRequested)return;pending.Dispose();pending=null;BeginRecording(options);}catch(OperationCanceledException){}}
  void BeginRecording(RecorderOptions options){
   try{
    recorder=Recorder.CreateRecorder(options);
    recorder.OnFrameRecorded+=(s,e)=>Interlocked.Exchange(ref frames,e.FrameNumber);
    recorder.OnRecordingFailed+=(s,e)=>dispatch(()=>Failed(e.Error));
    recorder.OnRecordingComplete+=(s,e)=>dispatch(()=>Complete());
    recorder.OnStatusChanged+=(s,e)=>dispatch(()=>{if(recorder==null)return;string value=e.Status.ToString();if(value=="Recording"){state="Recording";if(!snapshot)clock.Start();}else if(value=="Paused"){state="Paused";clock.Stop();}});
    lastSafetyUtc=DateTime.UtcNow;recorder.Record(nativeTarget);safetyTimer.Start();
   }catch(Exception ex){Failed(ex.Message);}
  }
  public void Pause(){if(recorder==null||state!="Recording")throw new IOException("Запись сейчас не выполняется.");recorder.Pause();clock.Stop();state="Paused";}
  public void Resume(){if(recorder==null||state!="Paused")throw new IOException("Запись не находится на паузе.");recorder.Resume();clock.Start();state="Recording";}
  public Task<Dictionary<string,object>> Stop(){if(pending!=null){pending.Cancel();pending.Dispose();pending=null;state="Idle";completion.TrySetResult(Data.Map("cancelled",true,"file","","elapsed",0,"frames",0));return completion.Task;}if(recorder==null){if(state=="Converting"&&completion!=null)return completion.Task;throw new IOException("Нет активной записи.");}if(state=="Stopping")return completion.Task;clock.Stop();state="Stopping";recorder.Stop();return completion.Task;}
  async void Complete(){
   safetyTimer.Stop();clock.Stop();var finished=recorder;recorder=null;
   try{if(finished!=null)finished.Dispose();if(nativeTarget!=target){state="Converting";await media.Remux(nativeTarget,target);}if(!File.Exists(target)||new FileInfo(target).Length==0)throw new IOException("Движок не создал файл.");lastFile=target;state="Complete";completion.TrySetResult(Data.Map("file",Path.GetFileName(target),"path",target,"bytes",new FileInfo(target).Length,"preview",target.StartsWith(paths.Preview,StringComparison.OrdinalIgnoreCase),"elapsed",clock.Elapsed.TotalSeconds,"frames",frames,"warning",warning));}
   catch(Exception ex){Failed(ex.Message);}
  }
  void Failed(string message){safetyTimer.Stop();clock.Stop();error=message;state="Error";var failed=recorder;recorder=null;if(failed!=null)failed.Dispose();if(completion!=null)completion.TrySetException(new IOException(message));}
  public void Dispose(){if(disposed)return;disposed=true;SystemEvents.PowerModeChanged-=OnPowerModeChanged;safetyTimer.Dispose();if(pending!=null){pending.Cancel();pending.Dispose();pending=null;}var r=recorder;recorder=null;if(r!=null){r.Stop();r.Dispose();}}
 }
}