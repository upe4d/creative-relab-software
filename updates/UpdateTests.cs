using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
namespace CreativeRelab.Updates
{
    // Проверяем недоверенные метаданные и целостность на собственных файлах; сеть не подменяет эти тесты.
    internal static class UpdateTests
    {
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
        private static Dictionary<string,object> Asset(string slug,string version){var tag=slug+"-v"+version;var name=slug+"-"+version+"-portable.exe";return new Dictionary<string,object>{{"name",name},{"size",100},{"digest","sha256:"+new string('a',64)},{"browser_download_url","https://github.com/"+ReleaseClient.Repository+"/releases/download/"+tag+"/"+name}};}
        private static Dictionary<string,object> Row(string slug,string version,bool preview=false,bool draft=false){return new Dictionary<string,object>{{"tag_name",slug+"-v"+version},{"prerelease",preview},{"draft",draft},{"assets",new[]{Asset(slug,version)}}};}
        internal static int Run()
        {
            var folder=Path.Combine(@"D:\Codex\work\CreativeRelab-Software\verification","updates-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var checks=new List<object>();
            Action<bool,string> check=(passed,name)=>{checks.Add(new{name,passed});if(!passed)throw new IOException("Не пройдена проверка: "+name);};
            Action<Action,string> reject=(action,name)=>{bool refused=false;try{action();}catch(InvalidDataException){refused=true;}catch(IOException){refused=true;}catch(ArgumentException){refused=true;}check(refused,name);};
            var care=Product.Get("pc-care");var guard=Product.Get("pc-guard");
            check(ReleaseClient.Normalize("1.2")==new Version(1,2,0,0),"Сравнение нормализует два компонента");check(ReleaseClient.Normalize("1.10.0")>ReleaseClient.Normalize("1.9.9"),"Версии сравниваются численно");
            var rows=new[]{Row("pc-care","1.9.0"),Row("pc-care","1.10.0"),Row("pc-care","2.0.0",true),Row("pc-care","9.0.0",false,true),Row("pc-guard","99.0.0",true)};
            check(ReleaseClient.Parse(Json.Serialize(rows),care).Version==new Version(1,10,0,0),"Стабильный канал игнорирует preview, draft и другой продукт");
            check(ReleaseClient.Parse(Json.Serialize(new[]{Row("pc-guard","3.6.0",true),Row("pc-guard","3.5.0")}),guard).Preview,"Канал Guard видит предварительные выпуски");
            reject(()=>ReleaseClient.Parse("{}",care),"Ответ-объект вместо массива отклонён");reject(()=>ReleaseClient.Parse("[]",care),"Пустой список не объявляется актуальной версией");reject(()=>ReleaseClient.Parse(Json.Serialize(new[]{Row("pc-tune","1.0.0")}),care),"Чужой продукт не выбирается");
            foreach(var invalid in new[]{"http://github.com/upe4d/creative-relab-software/releases/download/pc-care-v1.0.0/pc-care-1.0.0-portable.exe","https://evil.example/payload.exe","https://github.com/other/repo/releases/download/pc-care-v1.0.0/pc-care-1.0.0-portable.exe","https://github.com/upe4d/creative-relab-software/releases/download/pc-care-v1.0.0/pc-care-1.0.0-portable.exe?replace=1"}){
                var row=Row("pc-care","1.0.0");var asset=Asset("pc-care","1.0.0");asset["browser_download_url"]=invalid;row["assets"]=new[]{asset};reject(()=>ReleaseClient.Parse(Json.Serialize(new[]{row}),care),"Чужой или изменённый адрес отклонён: "+invalid.Split('/')[2]);
            }
            foreach(var digest in new[]{"",new string('a',64),"sha256:"+new string('g',64),"sha256:abcd"}){var row=Row("pc-care","1.0.0");var asset=Asset("pc-care","1.0.0");asset["digest"]=digest;row["assets"]=new[]{asset};reject(()=>ReleaseClient.Parse(Json.Serialize(new[]{row}),care),"Неверный SHA-256 отклонён: "+digest.Length);}
            foreach(var size in new long[]{0,-1,536870913}){var row=Row("pc-care","1.0.0");var asset=Asset("pc-care","1.0.0");asset["size"]=size;row["assets"]=new[]{asset};reject(()=>ReleaseClient.Parse(Json.Serialize(new[]{row}),care),"Недопустимый размер: "+size);}
            var duplicate=Row("pc-care","1.0.0");duplicate["assets"]=new[]{Asset("pc-care","1.0.0"),Asset("pc-care","1.0.0")};reject(()=>ReleaseClient.Parse(Json.Serialize(new[]{duplicate}),care),"Два одноимённых файла не выбираются произвольно");
            var incomplete=Row("pc-care","2.0.0");incomplete["assets"]=new object[0];reject(()=>ReleaseClient.Parse(Json.Serialize(new[]{Row("pc-care","1.0.0"),incomplete}),care),"Незавершённый новый выпуск не подменяется старым без ошибки");
            reject(()=>ReleaseClient.SafeFolder(@"C:\NotAllowed"),"Запись на C отклонена");reject(()=>ReleaseClient.SafeFolder(@"D:\"),"Корень D не является папкой программы");check(ReleaseClient.SafeFolder(folder)==folder,"Собственная отдельная папка D разрешена");
            var bytes=new byte[4096];using(var random=RandomNumberGenerator.Create())random.GetBytes(bytes);var file=Path.Combine(folder,"fixture.bin");File.WriteAllBytes(file,bytes);string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
            check(ReleaseClient.VerifyFile(file,hash,bytes.Length),"Точные байты и размер подтверждены");check(!ReleaseClient.VerifyFile(file,hash,bytes.Length+1),"Несовпадение размера обнаружено");bytes[0]^=0xff;File.WriteAllBytes(file,bytes);check(!ReleaseClient.VerifyFile(file,hash,bytes.Length),"Изменение одного байта обнаружено");
            using(var cancellation=new CancellationTokenSource()) { cancellation.Cancel();bool cancelled=false;try{ReleaseClient.Latest(care,cancellation.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}check(cancelled,"Отмена проверки не ждёт сетевого таймаута");cancelled=false;try{ReleaseClient.Download(null,care,delegate(int value){},cancellation.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}check(cancelled,"Отмена загрузки не создаёт файл и не запускает программу");}
            var report=new{checks=checks.Count,failures=0,networkUsed=false,programInstalled=false,results=checks};File.WriteAllText(Path.Combine(folder,"results.json"),Json.Serialize(report));Console.WriteLine(Json.Serialize(new{checks=checks.Count,failures=0,report=Path.Combine(folder,"results.json")}));return 0;
        }
    }
}
