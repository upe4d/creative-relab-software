using System;
using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace PCRecord {
 internal sealed class MainWindow:Form {
  readonly Paths paths;readonly MediaTools media;readonly CaptureEngine engine;readonly WebView2 view=new WebView2();
  RecordingToolbar toolbar;readonly Timer toolbarTimer=new Timer{Interval=300};TestPattern testWindow;bool closingReady,closingBusy;readonly bool verifyControls;readonly bool verifyShell;readonly bool verifyGallery;bool shellVerificationStarted;string theme="dark";bool hotkeyAvailable;int hotkeyError;
  [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
  [DllImport("user32.dll")]static extern bool UnregisterHotKey(IntPtr hwnd,int id);
  public MainWindow(Paths p,bool verify=false,bool controls=false,bool gallery=false){verifyGallery=gallery;verifyControls=controls;verifyShell=verify;paths=p;media=new MediaTools(p);engine=new CaptureEngine(p,media,a=>{if(!IsDisposed)BeginInvoke(a);});toolbar=new RecordingToolbar(engine,()=>{Show();WindowState=FormWindowState.Normal;Activate();});toolbarTimer.Tick+=(s,e)=>{if(engine.IsVideoRecording&&engine.ShowToolbar){if(!toolbar.Visible)toolbar.Show();toolbar.RefreshStatus();}else toolbar.Hide();};toolbarTimer.Start();Text="PC Record — Creative Relab";ClientSize=new Size(1280,830);MinimumSize=new Size(880,650);StartPosition=FormStartPosition.CenterScreen;try{Icon=new Icon(Path.Combine(paths.Root,"app.ico"));}catch(IOException){}view.Dock=DockStyle.Fill;Controls.Add(view);Shown+=async(s,e)=>{using(var g=CreateGraphics()){float scale=g.DpiX/96f;var area=Screen.FromControl(this).WorkingArea;MinimumSize=new Size(Math.Min((int)(880*scale),area.Width-40),Math.Min((int)(650*scale),area.Height-60));ClientSize=new Size(Math.Min((int)(1280*scale),area.Width-80),Math.Min((int)(830*scale),area.Height-100));CenterToScreen();}await Initialize();};FormClosing+=OnClosing;}
  async Task Initialize(){
   try{
    var opts=new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-sync --no-first-run");
    var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(paths.State,"WebView2"),opts);
    await view.EnsureCoreWebView2Async(env);var core=view.CoreWebView2;
    core.Settings.AreDevToolsEnabled=false;core.Settings.IsStatusBarEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
    core.SetVirtualHostNameToFolderMapping("pcrecord.local",paths.Public,CoreWebView2HostResourceAccessKind.DenyCors);
    core.SetVirtualHostNameToFolderMapping("records.pcrecord.local",paths.Records,CoreWebView2HostResourceAccessKind.Allow);
    core.SetVirtualHostNameToFolderMapping("preview.pcrecord.local",paths.Preview,CoreWebView2HostResourceAccessKind.Allow);
    core.PermissionRequested+=(s,e)=>{e.State=CoreWebView2PermissionState.Deny;};
    core.DownloadStarting+=(s,e)=>{e.Cancel=true;};
    core.NewWindowRequested+=(s,e)=>{e.Handled=true;OpenSite(e.Uri);};
    core.NavigationStarting+=(s,e)=>{if(!IsLocal(e.Uri)){e.Cancel=true;OpenSite(e.Uri);}};
    core.WebMessageReceived+=Message;core.NavigationCompleted+=async(s,e)=>{if((verifyShell||verifyGallery)&&!shellVerificationStarted){shellVerificationStarted=true;if(verifyGallery)await VerifyGallery();else await VerifyOwnWindow();}};
    core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
    core.WebResourceRequested+=(s,e)=>{Uri uri;if(Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&!new[]{"pcrecord.local","records.pcrecord.local","preview.pcrecord.local"}.Contains(uri.Host)){e.Response=env.CreateWebResourceResponse(new MemoryStream(),403,"Forbidden","Content-Type: text/plain");}};
    if(verifyControls)await core.AddScriptToExecuteOnDocumentCreatedAsync("window.PCRecordVerifyControls=true;");NativeTheme.Apply(this,true);core.Navigate("https://pcrecord.local/index.html");
    hotkeyAvailable=RegisterHotKey(Handle,1,0x4002,0x78);if(!hotkeyAvailable)hotkeyError=Marshal.GetLastWin32Error();
   }catch(Exception ex){Data.Write(Path.Combine(paths.State,"startup-error.json"),Data.Map("error",ex.ToString()));MessageBox.Show(this,"Не удалось открыть PC Record: "+ex.Message,"PC Record",MessageBoxButtons.OK,MessageBoxIcon.Error);Close();}
  }
  // Снимки берутся из настоящего WebView2, без отрисовки макетов и без личных окон рабочего стола.
  async Task VerifyGallery(){
   string folder=Path.Combine(paths.Root,"verification","gallery");Directory.CreateDirectory(folder);var rows=new List<object>();
   try{await Task.Delay(1800);await Command("testWindow",new Dictionary<string,object>());await Task.Delay(500);
    await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#system-audio').checked=false;document.querySelector('#countdown').value='0';document.querySelector('[data-mode=\"Окно\"]').click();document.querySelector('#choose-source').click();");
    await Task.Delay(500);await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('#source-options button')].find(b=>b.textContent==='PC Record — тестовое окно').click();");await Task.Delay(2500);
    foreach(string page in new[]{"record","settings","about"}){
     await view.CoreWebView2.ExecuteScriptAsync("location.hash='"+page+"';");await Task.Delay(400);await view.CoreWebView2.ExecuteScriptAsync("window.scrollTo(0,0);");await Task.Delay(150);
     string output=Path.Combine(folder,"pc-record-"+page+".png");using(var stream=new FileStream(output,FileMode.Create,FileAccess.Write,FileShare.None))await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);
     rows.Add(Data.Map("page",page,"file",Path.GetFileName(output),"capture","Real running WebView2 CapturePreview"));
    }
    await view.CoreWebView2.ExecuteScriptAsync("location.hash='record';document.querySelector('#theme').click();window.scrollTo(0,0);");await Task.Delay(600);
    using(var stream=new FileStream(Path.Combine(folder,"pc-record-light.png"),FileMode.Create,FileAccess.Write,FileShare.None))await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);
    rows.Add(Data.Map("page","record-light","file","pc-record-light.png","capture","Real running WebView2 CapturePreview"));
    Data.Write(Path.Combine(folder,"gallery-result.json"),Data.Map("pass",true,"cases",rows,"clientWidth",ClientSize.Width,"clientHeight",ClientSize.Height));
   }catch(Exception ex){Data.Write(Path.Combine(folder,"gallery-result.json"),Data.Map("pass",false,"error",ex.ToString()));}
   finally{closingReady=true;Close();}
  }
  async Task VerifyOwnWindow(){
   string folder=Path.Combine(paths.Root,"verification");Directory.CreateDirectory(folder);
   try{
    await Task.Delay(1800);
    var args=Data.Map("kind","window","source",Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),"fps",30,"format","MP4","resolution","source","systemAudio",false,"microphone",false,"camera",false);
    var result=await engine.Start(args,true);byte[] bytes=await OwnedInput.ReadBytes(paths,Data.Text(result,"path"),16*1024*1024);
    using(var stream=new MemoryStream(bytes))using(var image=new Bitmap(stream)){if(image.Width<1000||image.Height<650)throw new IOException("Размер окна не соответствует настольному интерфейсу.");Data.Write(Path.Combine(folder,"native-window-result.json"),Data.Map("test","native-webview-window-capture","pass",true,"width",image.Width,"height",image.Height,"bytes",bytes.Length,"audio",false,"microphone",false,"camera",false));}
    File.WriteAllBytes(Path.Combine(folder,"native-window.png"),bytes);
   }catch(Exception ex){Data.Write(Path.Combine(folder,"native-window-result.json"),Data.Map("test","native-webview-window-capture","pass",false,"error",ex.ToString()));}
   finally{closingReady=true;Close();}
  }  static bool IsLocal(string value){Uri uri;return Uri.TryCreate(value,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.Host=="pcrecord.local";}
  void OpenSite(string value){Uri uri;if(Uri.TryCreate(value,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.Host=="tools.crrl.ru"&&uri.AbsolutePath.StartsWith("/software/",StringComparison.Ordinal))Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}
  async void Message(object sender,CoreWebView2WebMessageReceivedEventArgs e){
   string id="";
   try{
    if(!IsLocal(e.Source))return;string raw=e.WebMessageAsJson;if(raw.Length>12*1024*1024)throw new IOException("Сообщение слишком велико.");
    var message=Data.Json.Deserialize<Dictionary<string,object>>(raw);id=Data.Text(message,"id");if(id.Length>64)throw new IOException("Неверный ID.");
    var args=Data.Get(message,"args") as Dictionary<string,object>??new Dictionary<string,object>();
    object result=await Command(Data.Text(message,"method"),args);Reply(Data.Map("id",id,"ok",true,"result",result));
   }catch(Exception ex){Reply(Data.Map("id",id,"ok",false,"error",ex.Message));}
  }
  void Reply(object data){if(!IsDisposed&&view.CoreWebView2!=null)view.CoreWebView2.PostWebMessageAsJson(Data.Json.Serialize(data));}
  async void Observe(Task<Dictionary<string,object>> task){try{await task;}catch(Exception ex){Reply(Data.Map("event","captureError","error",ex.Message));}}
  async Task<object> Command(string method,Dictionary<string,object> args){
   switch(method){
    case "testResize":if(!verifyControls)throw new IOException("Режим испытаний выключен");using(var graphics=CreateGraphics()){int width=Data.Num(args,"width");if(width<880||width>1280)throw new IOException("Неверный размер теста");float scale=graphics.DpiX/96f;WindowState=FormWindowState.Normal;ClientSize=new Size((int)(width*scale),ClientSize.Height);}await Task.Delay(600);return Data.Map("width",ClientSize.Width,"height",ClientSize.Height,"state",WindowState.ToString());
    case "updates":{string helper=Path.Combine(paths.Root,"Creative Relab Updates.exe");if(!File.Exists(helper))throw new IOException("Модуль обновлений отсутствует в папке программы.");Process.Start(new ProcessStartInfo(helper,"--product pc-record --target "+MediaTools.Q(paths.Root)){UseShellExecute=true});return true;}
    case "testResult":if(!verifyControls)throw new IOException("Режим испытаний выключен");Directory.CreateDirectory(Path.Combine(paths.Root,"verification"));Data.Write(Path.Combine(paths.Root,"verification","controls-result.json"),args);if(engine.Busy)await engine.Stop();closingReady=true;BeginInvoke(new Action(Close));return true;
    case "hello":return Data.Map("version","1.0.0","hotkeyAvailable",hotkeyAvailable,"hotkeyError",hotkeyError,"status",engine.Status(),"devices",CaptureEngine.Devices(),"theme",theme,"native",true);
    case "sources":return CaptureEngine.Sources(Data.Text(args,"kind"));
    case "status":return engine.Status();
    case "start":if(!hotkeyAvailable)args["toolbar"]=true;Observe(engine.Start(args,false));return engine.Status();
    case "pause":engine.Pause();return engine.Status();
    case "resume":engine.Resume();return engine.Status();
    case "stop":return await engine.Stop();
    case "snapshot":return await engine.Start(args,true,Data.Flag(args,"preview"));
    case "region":{
     if(engine.Busy)throw new IOException("Остановите запись перед выбором области.");string display=Data.Text(args,"source");var screen=Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==display);if(screen==null)throw new IOException("Монитор не найден.");
     using(var picker=new RegionPicker(screen)){if(picker.ShowDialog(this)!=DialogResult.OK)return Data.Map("cancelled",true);var rect=picker.Selected;return Data.Map("x",rect.X,"y",rect.Y,"width",rect.Width,"height",rect.Height);}
    }
    case "library":return Directory.GetFiles(paths.Records).Where(f=>new[]{".mp4",".mkv",".png"}.Contains(Path.GetExtension(f).ToLowerInvariant())).Select(f=>new FileInfo(f)).Where(f=>(f.Attributes&FileAttributes.ReparsePoint)==0).OrderByDescending(f=>f.LastWriteTimeUtc).Take(1000).Select(f=>Data.Map("id",f.Name,"name",f.Name,"date",f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),"bytes",f.Length,"type",f.Extension==".png"?"image":"video")).ToArray();
    case "readImage":{
     string path=paths.Existing(Data.Text(args,"id"));if(!String.Equals(Path.GetExtension(path),".png",StringComparison.OrdinalIgnoreCase))throw new IOException("Редактор открывает PNG.");
     return "data:image/png;base64,"+Convert.ToBase64String(await OwnedInput.ReadBytes(paths,path,8*1024*1024));
    }
    case "saveImage":{
     string data=Data.Text(args,"data"),prefix="data:image/png;base64,";if(!data.StartsWith(prefix,StringComparison.Ordinal)||data.Length>11*1024*1024)throw new IOException("Неверное изображение или превышен размер 8 МБ.");
     byte[] bytes=Convert.FromBase64String(data.Substring(prefix.Length));if(bytes.Length<8||bytes[0]!=137||bytes[1]!=80||bytes[2]!=78||bytes[3]!=71)throw new IOException("Неверный заголовок PNG.");
     using(var stream=new MemoryStream(bytes))using(var image=new Bitmap(stream)){if((long)image.Width*image.Height>33000000)throw new IOException("Изображение превышает 33 Мп.");}
     string output=paths.NewRecording(".png");File.WriteAllBytes(output,bytes);return Path.GetFileName(output);
    }    case "probe":return await media.Probe(paths.Existing(Data.Text(args,"id")));
    case "openFile":Process.Start(new ProcessStartInfo(paths.Existing(Data.Text(args,"id"))){UseShellExecute=true});return true;
    case "openFolder":Process.Start(new ProcessStartInfo("explorer.exe",MediaTools.Q(paths.Records)){UseShellExecute=true});return true;
    case "trim":if(engine.Busy)throw new IOException("Дождитесь завершения записи.");return Path.GetFileName(await media.Trim(Data.Text(args,"id"),Convert.ToDouble(Data.Get(args,"start"),System.Globalization.CultureInfo.InvariantCulture),Convert.ToDouble(Data.Get(args,"end"),System.Globalization.CultureInfo.InvariantCulture)));
    case "theme":theme=Data.Text(args,"value")=="light"?"light":"dark";NativeTheme.Apply(this,theme=="dark");return true;
    case "testWindow":if(testWindow==null||testWindow.IsDisposed){testWindow=new TestPattern();testWindow.Show();}else testWindow.Activate();return true;
    default:throw new IOException("Неизвестная команда: "+method);
   }
  }
  protected override void WndProc(ref System.Windows.Forms.Message m){if(m.Msg==0x312&&m.WParam.ToInt32()==1&&view.CoreWebView2!=null){view.CoreWebView2.ExecuteScriptAsync("document.dispatchEvent(new CustomEvent('record-shortcut'))");}base.WndProc(ref m);}
  async void OnClosing(object sender,FormClosingEventArgs e){
   if(closingReady)return;
   if(engine.Busy){e.Cancel=true;if(closingBusy)return;if(MessageBox.Show(this,"Остановить запись и закрыть PC Record после сохранения?","PC Record",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;closingBusy=true;try{await engine.Stop();}catch(Exception ex){MessageBox.Show(this,ex.Message,"PC Record");}closingReady=true;Close();return;}
   closingReady=true;
  }
  protected override void Dispose(bool disposing){if(disposing){toolbarTimer.Dispose();if(toolbar!=null)toolbar.Dispose();if(hotkeyAvailable)UnregisterHotKey(Handle,1);if(testWindow!=null)testWindow.Dispose();engine.Dispose();view.Dispose();}base.Dispose(disposing);}
 }
}