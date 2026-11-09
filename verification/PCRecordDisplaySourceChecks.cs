using System;using System.IO;using System.Reflection;using System.Linq;using System.Collections.Generic;using System.Threading.Tasks;using System.Windows.Forms;
internal static class PCRecordDisplaySourceChecks {
 // На несколько секунд экран закрывается собственным искусственным окном; запись не публикуется.
 static readonly BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
 static object Call(object o,string name,params object[] args){return o.GetType().GetMethod(name,Flags).Invoke(o,args);}
 static Dictionary<string,object> Map(params object[] v){var d=new Dictionary<string,object>();for(int i=0;i<v.Length;i+=2)d[(string)v[i]]=v[i+1];return d;}
 [STAThread]static int Main(){
  string root=AppDomain.CurrentDomain.BaseDirectory;var assembly=Assembly.LoadFrom(Path.Combine(root,"PC Record.exe"));
  object paths=Activator.CreateInstance(assembly.GetType("PCRecord.Paths"),Flags,null,new object[]{root},null),media=Activator.CreateInstance(assembly.GetType("PCRecord.MediaTools"),Flags,null,new object[]{Activator.CreateInstance(assembly.GetType("PCRecord.Paths"),Flags,null,new object[]{root},null)},null);
  int exit=1;var rows=new List<object>();using(var form=(Form)Activator.CreateInstance(assembly.GetType("PCRecord.TestPattern"),true)){
   var screen=Screen.PrimaryScreen;form.FormBorderStyle=FormBorderStyle.None;form.StartPosition=FormStartPosition.Manual;form.Bounds=screen.Bounds;form.TopMost=true;form.ShowInTaskbar=false;form.Text="PC Record — проверка исходного размера экрана";
   object engine=Activator.CreateInstance(assembly.GetType("PCRecord.CaptureEngine"),Flags,null,new object[]{paths,media,(Action<Action>)(a=>form.BeginInvoke(a)),null},null);
   form.Shown+=async(s,e)=>{try{await Task.Delay(1000);
    foreach(string format in new[]{"MP4","MKV"}){
     var config=Map("kind","display","source",screen.DeviceName,"format",format,"fps",30,"resolution","source","systemAudio",false,"microphone",false,"camera",false,"cursor",false);
     var pending=(Task<Dictionary<string,object>>)Call(engine,"Start",config,false,false);await Task.Delay(3500);var result=await (Task<Dictionary<string,object>>)Call(engine,"Stop");await pending;string file=Convert.ToString(result["path"]);
     var meta=await (Task<Dictionary<string,object>>)Call(media,"Probe",file);
     await (Task<string>)Call(media,"Stream","ffmpeg.exe","-v error -xerror -i pipe:0 -f null -",file,null,90);
     var video=((System.Collections.IEnumerable)meta["streams"]).Cast<Dictionary<string,object>>().First(x=>Convert.ToString(x["codec_type"])=="video");
     if(Convert.ToInt32(video["width"])!=3120||Convert.ToInt32(video["height"])!=2080)throw new IOException("Ожидался размер 3120x2080, получен "+video["width"]+"x"+video["height"]);
     rows.Add(Map("test","display-source-"+format.ToLowerInvariant()+"-decode","status","pass","bytes",new FileInfo(file).Length,"metadata",meta));
    }exit=0;
   }catch(Exception ex){rows.Add(Map("test","display-source-harness","status","failed","error",ex.ToString()));}
   finally{((IDisposable)engine).Dispose();Directory.CreateDirectory(Path.Combine(root,"verification"));File.WriteAllText(Path.Combine(root,"verification","display-source-result.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(Map("cases",rows,"exitCode",exit,"expectedSizeSource","Earlier primary-display measurement: 3120x2080","scope","Own synthetic full-screen content; no audio or camera")));form.Close();}};Application.Run(form);
  }return exit;
 }
}

