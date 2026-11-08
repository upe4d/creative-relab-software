using System;using System.IO;using System.Threading;using System.Collections.Generic;using System.Web.Script.Serialization;
namespace PCGuard {
class JobRegression {
 static int Main(){try{
 var root=Path.Combine(@"D:\Codex\work\CreativeRelab-Software\verification\job-regression",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var store=new Store(root);var jobs=new Jobs(store);var checks=new List<object>();Action<bool,string> check=(ok,name)=>{checks.Add(new{name,passed=ok});if(!ok)throw new IOException(name);};
 string id=Guid.NewGuid().ToString("N"),prefix=Path.Combine(store.Data,"Jobs",id),result=prefix+".result.json";
 Store.WritePath(prefix+".request.json",Store.Map("id",id,"action","network"));var value=Store.Map("id",id,"action","network","state","running","created",(long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds);
 Store.WritePath(result,value);check(Store.Text(jobs.Get(id),"state")=="running","Обычное чтение результата");
 var hold=result+".held";File.Move(result,hold);Exception workerError=null;var thread=new Thread(()=>{try{Thread.Sleep(60);File.Move(hold,result);}catch(Exception e){workerError=e;}});thread.Start();var response=jobs.Get(id);thread.Join();check(workerError==null&&Store.Text(response,"id")==id,"Временное отсутствие известного результата восстанавливается без повтора операции");
 using(var handle=new FileStream(result,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
 var release=new Thread(()=>{Thread.Sleep(65);handle.Dispose();});release.Start();response=jobs.Get(id);release.Join();check(Store.Text(response,"state")=="running","Чтение переживает краткую внешнюю блокировку");}
 bool unknown=false;try{jobs.Get(Guid.NewGuid().ToString("N"));}catch(IOException){unknown=true;}check(unknown,"Неизвестное задание остаётся ошибкой");
 Store.WritePath(result,Store.Map("id","wrong","state","running"));bool invalid=false;try{jobs.Get(id);}catch(InvalidDataException){invalid=true;}check(invalid,"Чужой ID результата не принимается");
 Store.WritePath(result,Store.Map("id",id,"state","unexpected"));invalid=false;try{jobs.Get(id);}catch(InvalidDataException){invalid=true;}check(invalid,"Неизвестное состояние не превращается в бесконечное ожидание");
 File.WriteAllText(Path.Combine(root,"results.json"),new JavaScriptSerializer().Serialize(new{checks=checks.Count,failures=0,systemActionsExecuted=false,results=checks}));Console.WriteLine(root);return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}}
