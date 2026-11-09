using System;using System.IO;using System.Reflection;using System.Linq;using System.Collections.Generic;using System.Threading.Tasks;using System.Windows.Forms;using ScreenRecorderLib;
internal static class PCRecordDeviceChecks {
 // Сценарий вызывает настоящий движок уже собранного EXE; микрофон не включается.
 static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 static object Call(object target,string name,params object[] args){return target.GetType().GetMethod(name,Flags).Invoke(target,args);}
 static Task<Dictionary<string,object>> Start(object engine,Dictionary<string,object> config){return (Task<Dictionary<string,object>>)Call(engine,"Start",config,false,false);}
 static async Task<Dictionary<string,object>> Stop(object engine){return await (Task<Dictionary<string,object>>)Call(engine,"Stop");}
 static string Text(Dictionary<string,object> d,string key){return Convert.ToString(d[key]);}
 static Dictionary<string,object> Map(params object[] pairs){var d=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1];return d;}
 [STAThread]static int Main(){
  var assembly=Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PC Record.exe"));string root=AppDomain.CurrentDomain.BaseDirectory;
  object paths=Activator.CreateInstance(assembly.GetType("PCRecord.Paths"),Flags,null,new object[]{root},null);
  object media=Activator.CreateInstance(assembly.GetType("PCRecord.MediaTools"),Flags,null,new[]{paths},null);
  int exit=1;var rows=new List<object>();
  using(var form=(Form)Activator.CreateInstance(assembly.GetType("PCRecord.TestPattern"),true)){
   object engine=Activator.CreateInstance(assembly.GetType("PCRecord.CaptureEngine"),Flags,null,new object[]{paths,media,(Action<Action>)(a=>form.BeginInvoke(a)),null},null);
   form.TopMost=true;
   form.Shown+=async(s,e)=>{try{
    await Task.Delay(1000);var config=Map("kind","window","source",form.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),"fps",30,"format","MP4","resolution","source","systemAudio",false,"microphone",false,"camera",false);
    var loop=Recorder.GetSystemAudioLoopbackDevices().FirstOrDefault(d=>d.IsDefaultDevice)??Recorder.GetSystemAudioLoopbackDevices().FirstOrDefault();
    if(loop==null)rows.Add(Map("test","system-audio","status","unavailable"));
    else{var c=new Dictionary<string,object>(config);c["systemAudio"]=true;c["loopback"]=loop.DeviceName;c["systemVolume"]=100;using(var tone=Tone())using(var player=new System.Media.SoundPlayer(tone)){player.Load();var task=Start(engine,c);await Task.Delay(800);player.PlayLooping();await Task.Delay(4000);player.Stop();var result=await Stop(engine);await task;string file=Text(result,"path");await Decode(media,file);string pcm=Path.Combine(root,"Data","Temp","measured-audio-"+Guid.NewGuid().ToString("N")+".pcm");await (Task<string>)Call(media,"Stream","ffmpeg.exe","-v error -i pipe:0 -vn -ac 1 -ar 16000 -f s16le pipe:1",file,pcm,45);
     var owned=assembly.GetType("PCRecord.OwnedInput");byte[] bytes=await (Task<byte[]>)owned.GetMethod("ReadBytes",BindingFlags.Static|BindingFlags.Public).Invoke(null,new object[]{paths,pcm,16000000});double sum=0,peak=0;for(int i=0;i+1<bytes.Length;i+=2){double value=BitConverter.ToInt16(bytes,i)/32768.0;sum+=value*value;peak=Math.Max(peak,Math.Abs(value));}double rms=Math.Sqrt(sum/(bytes.Length/2));if(rms<.001)throw new IOException("Не обнаружен контрольный тон");rows.Add(Map("test","system-audio-tone-decode","status","pass","samples",bytes.Length/2,"seconds",bytes.Length/32000.0,"rms",rms,"peak",peak));
    }}
    var camera=Recorder.GetSystemVideoCaptureDevices().FirstOrDefault();
    if(camera==null)rows.Add(Map("test","camera","status","unavailable"));
    else foreach(bool overlay in new[]{false,true}){form.WindowState=FormWindowState.Normal;form.Show();form.Activate();await Task.Delay(400);var c=new Dictionary<string,object>(config);if(overlay){c["camera"]=true;c["cameraDevice"]=camera.DeviceName;}else{c["kind"]="camera";c["source"]=camera.DeviceName;}var task=Start(engine,c);await Task.Delay(4500);var result=await Stop(engine);await task;string file=Text(result,"path");await Decode(media,file);var meta=await (Task<Dictionary<string,object>>)Call(media,"Probe",file);rows.Add(Map("test",overlay?"camera-overlay-decode":"camera-alone-decode","status","pass","metadata",meta,"visualContentReviewed",false));}
    rows.Add(Map("test","microphone-speech","status","skipped-by-owner","microphoneOpened",false));exit=0;
   }catch(Exception ex){rows.Add(Map("test","device-harness","status","failed","error",ex.ToString()));}
   finally{((IDisposable)engine).Dispose();string dir=Path.Combine(root,"verification");Directory.CreateDirectory(dir);var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();File.WriteAllText(Path.Combine(dir,"devices-result.json"),serializer.Serialize(Map("exitCode",exit,"loadedProductionAssembly",true,"cases",rows,"note","Audio/camera media stay local; no microphone capture and no upload.")));form.Close();}
   };Application.Run(form);
  }return exit;
 }
 static Task<string> Decode(object media,string file){return (Task<string>)Call(media,"Stream","ffmpeg.exe","-v error -xerror -i pipe:0 -f null -",file,null,45);}
 static MemoryStream Tone(){var stream=new MemoryStream();var writer=new BinaryWriter(stream);int rate=44100,count=rate*2;writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+count*2);writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(rate);writer.Write(rate*2);writer.Write((short)2);writer.Write((short)16);writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));writer.Write(count*2);for(int i=0;i<count;i++)writer.Write((short)(1200*Math.Sin(2*Math.PI*440*i/rate)));writer.Flush();stream.Position=0;return stream;}
}

