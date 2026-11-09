using System;using System.IO;using System.Linq;using System.Drawing;using System.Collections.Generic;using System.Threading.Tasks;using System.Windows.Forms;using ScreenRecorderLib;
namespace PCRecord {
 // Полноэкранное искусственное окно закрывает личный рабочий стол на выбранном мониторе.
 internal static class DisplayVerification {
  public static int Run(Paths p){int exit=1;var rows=new List<object>();string folder=Path.Combine(p.Root,"verification");Directory.CreateDirectory(folder);
   using(var cover=new TestPattern()){var screen=Screen.PrimaryScreen;cover.Text="PC Record — искусственный экран для проверки";cover.FormBorderStyle=FormBorderStyle.None;cover.StartPosition=FormStartPosition.Manual;cover.Bounds=screen.Bounds;cover.TopMost=true;cover.ShowInTaskbar=false;
    var media=new MediaTools(p);var video=new CaptureEngine(p,media,a=>cover.BeginInvoke(a));var shots=new CaptureEngine(p,media,a=>cover.BeginInvoke(a));
    cover.Shown+=async(s,e)=>{try{await Task.Delay(1200);var config=Data.Map("kind","display","source",screen.DeviceName,"fps",30,"format","MP4","resolution","720","systemAudio",false,"microphone",false,"camera",false,"border",false,"cursor",false);
     var resultTask=video.Start(config,false);await Task.Delay(3500);var result=await video.Stop();await resultTask;await Decode(media,Data.Text(result,"path"));var meta=await media.Probe(Data.Text(result,"path"));rows.Add(Data.Map("test","real-display-720p-record-decode","status","pass","metadata",meta,"screenWidth",screen.Bounds.Width,"screenHeight",screen.Bounds.Height));
     config["kind"]="area";config["x"]=20;config["y"]=20;config["width"]=640;config["height"]=360;config["resolution"]="source";resultTask=video.Start(config,false);await Task.Delay(2500);result=await video.Stop();await resultTask;await Decode(media,Data.Text(result,"path"));meta=await media.Probe(Data.Text(result,"path"));var dimensions=((System.Collections.IEnumerable)meta["streams"]).Cast<Dictionary<string,object>>().First(x=>Data.Text(x,"codec_type")=="video");if(Data.Num(dimensions,"width")!=640||Data.Num(dimensions,"height")!=360)throw new IOException("Неверный размер области");rows.Add(Data.Map("test","real-area-640x360-record-decode","status","pass","metadata",meta));
     var window=Data.Map("kind","window","source",cover.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),"fps",30,"format","MP4","resolution","720","cursor",false);var task=video.Start(window,false);await Task.Delay(1000);
     using(var bar=new RecordingToolbar(video,()=>cover.Activate())){bar.Show();bar.Location=new Point(screen.Bounds.Left+(screen.Bounds.Width-bar.Width)/2,screen.Bounds.Top+100);bar.RefreshStatus();await Task.Delay(400);var rect=new Rectangle(bar.Left-screen.Bounds.Left,bar.Top-screen.Bounds.Top,bar.Width,bar.Height);int visible,excluded;
      config["kind"]="display";config["resolution"]="source";Recorder.SetExcludeFromCapture(bar.Handle,false);await Task.Delay(300);var shot=await shots.Start(config,true);using(var bitmap=await Image(p,Data.Text(shot,"path"))){visible=RedCount(bitmap,rect);using(var crop=bitmap.Clone(rect,bitmap.PixelFormat))crop.Save(Path.Combine(folder,"floating-toolbar-onscreen.png"),System.Drawing.Imaging.ImageFormat.Png);}
      bool accepted=Recorder.SetExcludeFromCapture(bar.Handle,true);await Task.Delay(300);shot=await shots.Start(config,true);using(var bitmap=await Image(p,Data.Text(shot,"path"))){excluded=RedCount(bitmap,rect);}
      await bar.StopRecording();await task;if(!accepted||visible<100||excluded>visible/10)throw new IOException("Исключение панели не подтверждено: visible="+visible+", excluded="+excluded+", accepted="+accepted);
      rows.Add(Data.Map("test","floating-toolbar-display-exclusion-pixel-test","status","pass","visibleRedPixels",visible,"excludedRedPixels",excluded,"apiAccepted",accepted,"x",rect.X,"y",rect.Y,"width",rect.Width,"height",rect.Height));
     }
     exit=0;
    }catch(Exception ex){rows.Add(Data.Map("test","display-verification","status","failed","error",ex.ToString()));}finally{video.Dispose();shots.Dispose();Data.Write(Path.Combine(folder,"display-result.json"),Data.Map("exitCode",exit,"cases",rows,"scope","Only a topmost synthetic full-screen PC Record window on primary display. No audio or camera."));cover.Close();}};Application.Run(cover);
   }return exit;
  }
  static async Task<Bitmap> Image(Paths p,string file){byte[] bytes=await OwnedInput.ReadBytes(p,file,16*1024*1024);using(var memory=new MemoryStream(bytes))using(var bitmap=new Bitmap(memory))return new Bitmap(bitmap);}
  static int RedCount(Bitmap image,Rectangle area){int count=0;for(int y=area.Top;y<area.Bottom;y+=2)for(int x=area.Left;x<area.Right;x+=2){Color c=image.GetPixel(x,y);if(c.R>170&&c.G>50&&c.G<170&&c.B>60&&c.B<190&&c.R>c.G*1.25)count++;}return count;}
  static Task<string> Decode(MediaTools media,string file){return media.Stream("ffmpeg.exe","-v error -xerror -i pipe:0 -f null -",file,null,60);}
 }
}
