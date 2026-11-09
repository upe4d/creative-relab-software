using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
namespace PCRecord {
 internal static class Data {
  public static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024};
  public static Dictionary<string,object> Map(params object[] v){var d=new Dictionary<string,object>();for(int i=0;i<v.Length;i+=2)d.Add((string)v[i],v[i+1]);return d;}
  public static object Get(Dictionary<string,object>d,string k,object fallback=null){object v;return d!=null&&d.TryGetValue(k,out v)?v:fallback;}
  public static string Text(Dictionary<string,object>d,string k,string fallback=""){return Convert.ToString(Get(d,k,fallback),CultureInfo.InvariantCulture);}
  public static bool Flag(Dictionary<string,object>d,string k){return Convert.ToBoolean(Get(d,k,false),CultureInfo.InvariantCulture);}
  public static int Num(Dictionary<string,object>d,string k,int fallback=0){return Convert.ToInt32(Get(d,k,fallback),CultureInfo.InvariantCulture);}
  public static void Write(string path,object value){File.WriteAllText(path,Json.Serialize(value),new System.Text.UTF8Encoding(false));}
 }
 // Изменяемые каталоги расположены на D, пути через ссылки отвергаются.
 internal sealed class Paths {
  public readonly string Root,Public,State,Temp,Records,Preview;
  public Paths(string root){Root=Path.GetFullPath(root);RequireD(Root);RejectLinks(Root);Public=Path.Combine(Root,"public");State=Path.Combine(Root,"Data");Temp=Path.Combine(State,"Temp");Records=Path.Combine(Root,"Recordings");Preview=Path.Combine(State,"Preview");foreach(string p in new[]{Root,State,Temp,Records,Preview}){RejectLinks(p);Directory.CreateDirectory(p);}}
  public static void RequireD(string p){if(!String.Equals(Path.GetPathRoot(Path.GetFullPath(p)),@"D:\",StringComparison.OrdinalIgnoreCase))throw new IOException("PC Record сохраняет файлы только на D.");}
  public static void RejectLinks(string p){for(var d=new DirectoryInfo(Path.GetFullPath(p));d!=null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Путь через ссылку не разрешён: "+d.FullName);}
  public string NewRecording(string ext){return Path.Combine(Records,"PC Record "+DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+ext);}
  public string Existing(string id){if(String.IsNullOrWhiteSpace(id)||id.Length>200||Path.GetFileName(id)!=id)throw new IOException("Неверный идентификатор файла.");string p=Path.Combine(Records,id);RejectLinks(p);if(!File.Exists(p))throw new FileNotFoundException("Запись не найдена.");return p;}
  public long FreeBytes(){return new DriveInfo("D").AvailableFreeSpace;}
 }
 internal sealed class MediaTools {
  readonly Paths paths;
  public MediaTools(Paths p){paths=p;}
  // Файлы открывает управляемая часть; кодек получает потоки вместо путей к файлам.
  public async Task<string> Stream(string exe,string args,string input,string output,int seconds){
   if(input!=null){Paths.RequireD(input);Paths.RejectLinks(input);}if(output!=null){Paths.RequireD(output);Paths.RejectLinks(output);}
   var info=new ProcessStartInfo(Path.Combine(paths.Root,"tools",exe),args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=paths.Root};
   string tempOutput=output==null?null:Path.Combine(paths.Temp,"media-"+Guid.NewGuid().ToString("N")+".partial");
   using(var process=new Process{StartInfo=info})using(var reader=input==null?null:new OwnedMediaServer(paths,input))using(var writer=tempOutput==null?null:new FileStream(tempOutput,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
    if(reader!=null)process.StartInfo.Arguments=args.Replace("pipe:0",Q(reader.Url));process.Start();var stderr=ReadLimited(process.StandardError,4096);
    Task<string> stdout=writer==null?ReadLimited(process.StandardOutput,1024*1024):null;
    Task copyOutput=writer==null?Task.FromResult(0):process.StandardOutput.BaseStream.CopyToAsync(writer);
    Task copyInput=Feed(null,process.StandardInput.BaseStream);
    bool exited=await Task.Run(()=>process.WaitForExit(seconds*1000));
    if(!exited){try{process.Kill();}catch(InvalidOperationException){}throw new IOException("Превышено время обработки медиа.");}
    string error=await stderr;await copyOutput;await copyInput;
    if(process.ExitCode!=0)throw new IOException("Обработка медиа завершилась ошибкой: "+error);
    if(writer!=null){writer.Flush();writer.Close();File.Move(tempOutput,output);}
    return stdout==null?"":await stdout;
   }
  }
  static async Task Feed(Stream source,Stream destination){try{if(source!=null)await source.CopyToAsync(destination);}catch(IOException){}finally{try{destination.Close();}catch(IOException){}}}
  static async Task<string> ReadLimited(StreamReader reader,int limit){var text=new System.Text.StringBuilder();var buffer=new char[1024];int n;while((n=await reader.ReadAsync(buffer,0,buffer.Length))>0){text.Append(buffer,0,n);if(text.Length>limit)text.Remove(0,text.Length-limit);}return text.ToString();}
  public async Task<Dictionary<string,object>> Probe(string file){
   string text=await Stream("ffprobe.exe","-v error -show_entries format=duration:stream=codec_type,codec_name,width,height,avg_frame_rate,nb_frames -of json -i pipe:0",file,null,30);
   var result=Data.Json.Deserialize<Dictionary<string,object>>(text);var format=result["format"] as Dictionary<string,object>;
   if(format!=null&&!format.ContainsKey("duration")){
    string counted=await Stream("ffprobe.exe","-v error -count_frames -show_entries stream=codec_type,avg_frame_rate,nb_read_frames -of json -i pipe:0",file,null,120);
    var details=Data.Json.Deserialize<Dictionary<string,object>>(counted);
    foreach(object item in (System.Collections.IEnumerable)details["streams"]){var stream=item as Dictionary<string,object>;if(stream==null||Data.Text(stream,"codec_type")!="video")continue;string[] rate=Data.Text(stream,"avg_frame_rate").Split('/');double frames,n,d;if(rate.Length==2&&Double.TryParse(Data.Text(stream,"nb_read_frames"),NumberStyles.Float,CultureInfo.InvariantCulture,out frames)&&Double.TryParse(rate[0],NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&Double.TryParse(rate[1],NumberStyles.Float,CultureInfo.InvariantCulture,out d)&&n>0&&d>0){format["duration"]=(frames*d/n).ToString("0.######",CultureInfo.InvariantCulture);result["durationSource"]="decoded-frame-count";break;}}
   }return result;
  }
  public async Task<string> Remux(string input,string output){await Stream("ffmpeg.exe","-hide_banner -loglevel error -i pipe:0 -map 0 -c copy -f matroska pipe:1",input,output,120);return output;}
  public async Task<string> Trim(string id,double start,double end){string input=paths.Existing(id);if(!new[]{".mp4",".mkv"}.Contains(Path.GetExtension(input).ToLowerInvariant()))throw new IOException("Обрезка поддерживает MP4 и MKV.");var meta=await Probe(input);var format=(Dictionary<string,object>)meta["format"];double duration=Double.Parse(Convert.ToString(format["duration"],CultureInfo.InvariantCulture),CultureInfo.InvariantCulture);if(Double.IsNaN(start)||Double.IsNaN(end)||Double.IsInfinity(start)||Double.IsInfinity(end)||start<0||end<=start||end>duration+.1)throw new IOException("Границы фрагмента должны находиться внутри записи.");string output=paths.NewRecording(".mp4");await Stream("ffmpeg.exe","-hide_banner -loglevel error -i pipe:0 -ss "+start.ToString("0.###",CultureInfo.InvariantCulture)+" -t "+(end-start).ToString("0.###",CultureInfo.InvariantCulture)+" -c:v libopenh264 -b:v 8000000 -pix_fmt yuv420p -c:a aac -movflags frag_keyframe+empty_moov+default_base_moof -f mp4 pipe:1",input,output,Math.Min(600,(int)(end-start)*3+60));return output;}
  public static string Q(string value){if(value.IndexOf('"')>=0||value.IndexOf('\r')>=0||value.IndexOf('\n')>=0)throw new IOException("Неверный путь.");return "\""+value+"\"";}
 }
}