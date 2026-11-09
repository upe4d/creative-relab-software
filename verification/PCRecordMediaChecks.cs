using System;using System.IO;using System.Linq;using System.Reflection;using System.Collections.Generic;using System.Globalization;using System.Drawing;using System.Security.Cryptography;using System.Threading.Tasks;
internal static class PCRecordMediaChecks {
 static readonly BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
 static Assembly assembly;
 static object Create(string type,params object[] args){return Activator.CreateInstance(assembly.GetType("PCRecord."+type),Flags,null,args,null);}
 static object Call(object target,string method,params object[] args){return target.GetType().GetMethod(method,Flags).Invoke(target,args);}
 static string Text(Dictionary<string,object> d,string k){return Convert.ToString(d[k],CultureInfo.InvariantCulture);}
 static Dictionary<string,object> Map(params object[] pairs){var d=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1];return d;}
 static Task<Dictionary<string,object>> Probe(object media,string file){return (Task<Dictionary<string,object>>)Call(media,"Probe",file);}
 static IEnumerable<Dictionary<string,object>> Streams(Dictionary<string,object> meta){return ((System.Collections.IEnumerable)meta["streams"]).Cast<Dictionary<string,object>>();}
 static Task<string> Run(object media,string arguments,string input,string output=null,int seconds=60){return (Task<string>)Call(media,"Stream","ffmpeg.exe",arguments,input,output,seconds);}
 static Task<byte[]> Bytes(object paths,string file){return (Task<byte[]>)assembly.GetType("PCRecord.OwnedInput").GetMethod("ReadBytes",BindingFlags.Static|BindingFlags.Public).Invoke(null,new object[]{paths,file,16000000});}
 static int Main(string[] args){try{assembly=Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PC Record.exe"));return Check(args).GetAwaiter().GetResult();}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static async Task<int> Check(string[] args){
  string root=AppDomain.CurrentDomain.BaseDirectory;object paths=Create("Paths",root),media=Create("MediaTools",paths);string folder=Path.Combine(root,"verification");Directory.CreateDirectory(folder);var rows=new List<object>();
  if(args.Length==2&&args[0]=="--decode"){string input=(string)Call(paths,"Existing",args[1]);var details=await Probe(media,input);await Run(media,"-v error -xerror -i pipe:0 -f null -",input,null,180);Console.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(Map("test","full-decode","status","pass","metadata",details)));return 0;}
  if(args.Length==2&&args[0]=="--probe"){var details=await Probe(media,(string)Call(paths,"Existing",args[1]));Console.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(details));return 0;}
  if(args.Length==2&&(args[0]=="--long"||args[0]=="--samples")){
   string file=(string)Call(paths,"Existing",args[1]);var meta=await Probe(media,file);double duration=Double.Parse(Text((Dictionary<string,object>)meta["format"],"duration"),CultureInfo.InvariantCulture);var hashes=new HashSet<string>();
   double[] points=args[0]=="--samples"?new[]{duration*.1,duration*.5,duration*.9}:new[]{10.0,1800.0,3600.0,5400.0,duration-10};
   foreach(double point in points){
    string bmp=Path.Combine(root,"Data","Temp","long-frame-"+point.ToString("0",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")+".bmp");await Run(media,"-v error -ss "+point.ToString("0.###",CultureInfo.InvariantCulture)+" -i pipe:0 -frames:v 1 -f image2pipe -vcodec bmp pipe:1",file,bmp,90);
    byte[] bytes=await Bytes(paths,bmp);string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");hashes.Add(hash);int bright=0,cyan=0;
    using(var memory=new MemoryStream(bytes))using(var image=new Bitmap(memory)){for(int y=0;y<image.Height;y+=4)for(int x=0;x<image.Width;x+=4){var c=image.GetPixel(x,y);if(c.R>150&&c.G>150&&c.B>150)bright++;if(c.R<100&&c.G>100&&c.B>140)cyan++;}if(bright<30||cyan<30)throw new IOException("Не найдено искусственное содержимое на отметке "+point);image.Save(Path.Combine(folder,"long-frame-"+point.ToString("0",CultureInfo.InvariantCulture)+".png"),System.Drawing.Imaging.ImageFormat.Png);}
    rows.Add(Map("test","long-frame-sample","seconds",point,"sha256",hash,"brightPixels",bright,"cyanPixels",cyan,"status","pass"));
   }
   if(hashes.Count!=points.Length)throw new IOException("Кадры разных участков записи совпали");
   File.WriteAllText(Path.Combine(folder,args[0]=="--samples"?"short-samples-result.json":"long-samples-result.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(Map("cases",rows,"distinctFrames",hashes.Count,"exitCode",0)));return 0;
  }
  string audioFile=null;foreach(string f in Directory.GetFiles(Path.Combine(root,"Recordings"),"*.mp4")){var meta=await Probe(media,f);if(Streams(meta).Any(x=>Text(x,"codec_type")=="audio")){audioFile=f;break;}}
  if(audioFile==null)throw new IOException("Нет контрольной записи со звуком");
  string clip=await (Task<string>)Call(media,"Trim",Path.GetFileName(audioFile),.3,2.3);var cut=await Probe(media,clip);if(!Streams(cut).Any(x=>Text(x,"codec_type")=="video")||!Streams(cut).Any(x=>Text(x,"codec_type")=="audio"))throw new IOException("При обрезке потерян поток");
  await Run(media,"-v error -xerror -i pipe:0 -f null -",clip);
  string pcm=Path.Combine(root,"Data","Temp","trim-audio-"+Guid.NewGuid().ToString("N")+".pcm");await Run(media,"-v error -i pipe:0 -vn -ac 1 -ar 16000 -f s16le pipe:1",clip,pcm);byte[] data=await Bytes(paths,pcm);double sum=0;for(int i=0;i+1<data.Length;i+=2){double v=BitConverter.ToInt16(data,i)/32768.0;sum+=v*v;}double rms=Math.Sqrt(sum/(data.Length/2));if(rms<.001)throw new IOException("После обрезки исчез контрольный тон");
  double cutDuration=Double.Parse(Text((Dictionary<string,object>)cut["format"],"duration"),CultureInfo.InvariantCulture);if(Math.Abs(cutDuration-2)>.2)throw new IOException("Неверная длина обрезки со звуком");rows.Add(Map("test","trim-audio-video-decode","status","pass","duration",cutDuration,"audioRms",rms,"metadata",cut));
  string secondRoot=args[0];object secondPaths=Create("Paths",secondRoot),secondMedia=Create("MediaTools",secondPaths);string high=null;
  foreach(string f in Directory.GetFiles(Path.Combine(secondRoot,"Recordings"),"*.mp4")){var meta=await Probe(secondMedia,f);if(Streams(meta).Any(x=>Text(x,"codec_type")=="video"&&Text(x,"avg_frame_rate")=="60/1"&&Convert.ToInt32(x["width"])==1920)){high=f;break;}}
  if(high==null)throw new IOException("Нет контрольной 1080p60 записи");clip=await (Task<string>)Call(secondMedia,"Trim",Path.GetFileName(high),.2,1.2);cut=await Probe(secondMedia,clip);await Run(secondMedia,"-v error -xerror -i pipe:0 -f null -",clip);var video=Streams(cut).First(x=>Text(x,"codec_type")=="video");if(Convert.ToInt32(video["width"])!=1920||Convert.ToInt32(video["height"])!=1080)throw new IOException("Изменился размер фрагмента");rows.Add(Map("test","trim-1080p60-decode","status","pass","metadata",cut));
  File.WriteAllText(Path.Combine(folder,"media-result.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(Map("cases",rows,"exitCode",0)));return 0;
 }
}

