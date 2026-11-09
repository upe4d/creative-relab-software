using System;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace PCRecord {
 internal static class Verification {
  public static int Run(Paths paths){
   int exit=1;Directory.CreateDirectory(Path.Combine(paths.Root,"verification"));var report=new List<object>();
   using(var form=new TestPattern()){
    var media=new MediaTools(paths);var engine=new CaptureEngine(paths,media,a=>form.BeginInvoke(a));
    form.Shown+=async(s,e)=>{
     try{
      await Task.Delay(1200);
      var args=Data.Map("kind","window","source",form.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),"format","MP4","fps",30,"resolution","source","systemAudio",false,"microphone",false,"camera",false);
      var shot=await Deadline(engine.Start(args,true),25);string png=Data.Text(shot,"path");
      using(var image=new Bitmap(new MemoryStream(await OwnedInput.ReadBytes(paths,png,16*1024*1024)))){int bright=0,cyan=0;for(int y=0;y<image.Height;y+=4)for(int x=0;x<image.Width;x+=4){Color c=image.GetPixel(x,y);if(c.R>150&&c.G>150&&c.B>150)bright++;if(c.B>140&&c.G>100&&c.R<100)cyan++;}if(bright<30||cyan<30)throw new IOException("Тестовое окно не распознано в снимке: bright="+bright+", cyan="+cyan);report.Add(Data.Map("test","real-window-png","pass",true,"file",png,"width",image.Width,"height",image.Height,"brightPixels",bright,"cyanPixels",cyan));}
      var videoTask=engine.Start(args,false);await Task.Delay(2000);engine.Pause();if(Data.Text(engine.Status(),"state")!="Paused")throw new IOException("Пауза не подтверждена");await Task.Delay(1000);engine.Resume();await Task.Delay(2000);var video=await Deadline(engine.Stop(),30);await videoTask;
      string mp4=Data.Text(video,"path");var info=await media.Probe(mp4);double duration=Duration(info);if(duration<2.8||duration>7)throw new IOException("Неправильная длительность после паузы: "+duration);
      await media.Stream("ffmpeg.exe","-v error -xerror -i pipe:0 -f null -",mp4,null,30);
      string frame=Path.Combine(paths.Root,"verification","captured-frame-"+Guid.NewGuid().ToString("N").Substring(0,8)+".bmp");await media.Stream("ffmpeg.exe","-v error -i pipe:0 -ss 1 -frames:v 1 -f image2pipe -vcodec bmp pipe:1",mp4,frame,30);
      report.Add(Data.Map("test","mp4-h264-pause-resume-decode","pass",true,"file",mp4,"duration",duration,"metadata",info,"decodedFrame",frame));
      args["format"]="MKV";var mkvTask=engine.Start(args,false);await Task.Delay(1800);var mkv=await Deadline(engine.Stop(),30);await mkvTask;var mkvInfo=await media.Probe(Data.Text(mkv,"path"));await media.Stream("ffmpeg.exe","-v error -xerror -i pipe:0 -f null -",Data.Text(mkv,"path"),null,30);report.Add(Data.Map("test","mkv-real-record-remux-decode","pass",true,"file",Data.Text(mkv,"path"),"metadata",mkvInfo));
      string trim=await media.Trim(Path.GetFileName(mp4),.5,1.5);var trimInfo=await media.Probe(trim);double cutDuration=Duration(trimInfo);if(cutDuration<.8||cutDuration>1.3)throw new IOException("Некорректная длительность обрезки");await media.Stream("ffmpeg.exe","-v error -xerror -i pipe:0 -f null -",trim,null,30);report.Add(Data.Map("test","trim-one-second-decode","pass",true,"file",trim,"duration",cutDuration));
      string mkvTrim=await media.Trim(Path.GetFileName(Data.Text(mkv,"path")),.2,1.2);var mkvTrimInfo=await media.Probe(mkvTrim);if(Math.Abs(Duration(mkvTrimInfo)-1)>.3)throw new IOException("Неправильная обрезка MKV");await media.Stream("ffmpeg.exe","-v error -xerror -i pipe:0 -f null -",mkvTrim,null,30);report.Add(Data.Map("test","mkv-duration-and-trim-decode","pass",true,"file",mkvTrim,"duration",Duration(mkvTrimInfo)));bool rejected=false;try{paths.Existing("..\\outside.mp4");}catch(IOException){rejected=true;}if(!rejected)throw new IOException("Не отклонён выход за папку записей");report.Add(Data.Map("test","reject-library-path-traversal","pass",true));
      rejected=false;var invalid=Data.Map("kind","window","source","0","fps",30);try{await engine.Start(invalid,false);}catch(IOException){rejected=true;}if(!rejected)throw new IOException("Не отклонено недоступное окно");report.Add(Data.Map("test","reject-invalid-window","pass",true));
      exit=0;
     }catch(Exception ex){report.Add(Data.Map("test","verification-error","pass",false,"error",ex.ToString()));}
     finally{Data.Write(Path.Combine(paths.Root,"verification","capture-result.json"),Data.Map("date",DateTime.Now.ToString("yyyy-MM-dd"),"scope","Only synthetic PC Record test window; no microphone, camera or system audio capture","cases",report,"exitCode",exit));engine.Dispose();form.Close();}
    };
    Application.Run(form);
   }return exit;
  }
  static async Task<T> Deadline<T>(Task<T> operation,int seconds){if(await Task.WhenAny(operation,Task.Delay(seconds*1000))!=operation)throw new IOException("Истекло время ожидания записи.");return await operation;}
  static double Duration(Dictionary<string,object> info){return Double.Parse(Convert.ToString(((Dictionary<string,object>)info["format"])["duration"],CultureInfo.InvariantCulture),CultureInfo.InvariantCulture);}
 }
}