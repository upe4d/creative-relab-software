using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace PCGuard
{
    // Загружаются только текстовые IOC из одного закреплённого репозитория; код и образцы не исполняются.
    internal sealed class RuleUpdater
    {
        internal const string Repository="PaloAltoNetworks/Unit42-timely-threat-intel";
        internal const string SourcePrefix="https://github.com/"+Repository+"/blob/";
        private readonly Store store;private readonly Engine engine;private readonly Func<string,CancellationToken,byte[]> download;
        private static readonly SemaphoreSlim UpdateGate=new SemaphoreSlim(1,1);
        internal RuleUpdater(Store store,Engine engine,Func<string,CancellationToken,byte[]> transport=null){this.store=store;this.engine=engine;download=transport??Fetch;}
        private static byte[] Fetch(string url,CancellationToken cancel)
        {
            var uri=new Uri(url);if(uri.Scheme!="https"||(uri.Host!="api.github.com"&&uri.Host!="raw.githubusercontent.com"))throw new IOException("Недопустимый источник базы.");
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            var request=(HttpWebRequest)WebRequest.Create(uri);request.UserAgent="PCGuard/3.3";request.Timeout=20000;request.ReadWriteTimeout=20000;request.AllowAutoRedirect=false;request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
            using(var registration=cancel.Register(request.Abort))using(var response=(HttpWebResponse)request.GetResponse())using(var input=response.GetResponseStream())using(var output=new MemoryStream())
            {
                if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("Источник базы вернул HTTP "+(int)response.StatusCode);
                byte[] chunk=new byte[32768];int got;while((got=input.Read(chunk,0,chunk.Length))>0){cancel.ThrowIfCancellationRequested();if(output.Length+got>6*1024*1024)throw new IOException("Ответ источника превышает 6 МиБ.");output.Write(chunk,0,got);}return output.ToArray();
            }
        }
        private Dictionary<string,object> Json(string url,CancellationToken cancel){byte[] bytes=download(url,cancel);if(bytes.Length>6*1024*1024)throw new IOException("Слишком большой каталог базы.");return Store.Object(Store.Serializer().DeserializeObject(Store.Utf8.GetString(bytes)));}
        private object Checked(string commit,bool changed,int count)
        {
            var state=Store.Map("checked",DateTimeOffset.Now.ToString("o"),"commit",commit,"repository",Repository,"changed",changed,"downloadedFiles",count);
            store.Write("rule-update-state.json",state);return Store.Map("message",changed?"Собственная база PC Guard обновлена. Новые индикаторы кампаний дают предупреждение, а не автоматический карантин.":"В источнике Unit 42 новых изменений нет. Собственная база PC Guard уже содержит этот выпуск.","update",state,"engine",engine.Info());
        }
        internal object Update(CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();if(!UpdateGate.Wait(0))throw new InvalidOperationException("Обновление собственной базы уже выполняется.");
            try{using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel,CancellationToken.None)){deadline.CancelAfter(180000);return UpdateCore(deadline.Token);}}
            finally{UpdateGate.Release();}
        }
        private object UpdateCore(CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var last=store.Read("rule-update-state.json");DateTimeOffset checkedAt;
            if(DateTimeOffset.TryParse(Store.Text(last,"checked"),out checkedAt)&&DateTimeOffset.Now>=checkedAt&&DateTimeOffset.Now-checkedAt<TimeSpan.FromSeconds(60)&&Store.Text(last,"commit")==engine.ManagedFeedCommit())return Store.Map("message","Источник проверялся менее минуты назад.","update",last,"engine",engine.Info());
            var commitInfo=Json("https://api.github.com/repos/"+Repository+"/commits/main",cancel);string commit=Store.Text(commitInfo,"sha");
            if(!Regex.IsMatch(commit,@"\A[a-f0-9]{40}\z"))throw new IOException("Источник не вернул допустимый commit.");
            cancel.ThrowIfCancellationRequested();if(engine.ManagedFeedCommit()==commit)return Checked(commit,false,0);
            var tree=Json("https://api.github.com/repos/"+Repository+"/git/trees/"+commit+"?recursive=1",cancel);
            if(Object.Equals(Store.Get(tree,"truncated"),true))throw new IOException("Каталог источника неполный. Предыдущая база сохранена.");
            var files=Store.Array(Store.Get(tree,"tree")).Select(Store.Object).Where(r=>Store.Text(r,"type")=="blob"&&Regex.IsMatch(Store.Text(r,"path"),@"\A\d{4}-\d{2}-\d{2}[^/\\]{0,200}\.txt\z")).ToArray();
            if(files.Length==0||files.Length>2000)throw new IOException("Неожиданный объём каталога IOC. Предыдущая база сохранена.");
            var rows=new ConcurrentBag<Dictionary<string,object>>();long total=0;int rowCount=0;
            try{Parallel.ForEach(files,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=cancel},file=>{
                string path=Store.Text(file,"path");if(Convert.ToInt64(Store.Get(file,"size",0))>2*1024*1024)throw new IOException("Текст IOC больше 2 МиБ.");
                byte[] bytes=download("https://raw.githubusercontent.com/"+Repository+"/"+commit+"/"+Uri.EscapeDataString(path),cancel);
                if(bytes.Length>2*1024*1024||Interlocked.Add(ref total,bytes.Length)>32*1024*1024)throw new IOException("Достигнут предел загрузки текстовых IOC.");
                // Проверяется объект Git, указанный каталогом закреплённого commit.
                string gitHash;using(var hash=System.Security.Cryptography.SHA1.Create())using(var blob=new MemoryStream()){byte[] header=Encoding.ASCII.GetBytes("blob "+bytes.Length+"\0");blob.Write(header,0,header.Length);blob.Write(bytes,0,bytes.Length);blob.Position=0;gitHash=BitConverter.ToString(hash.ComputeHash(blob)).Replace("-","").ToLowerInvariant();}
                if(!gitHash.Equals(Store.Text(file,"sha"),StringComparison.Ordinal))throw new IOException("Текст IOC не совпал с объектом Git. Предыдущая база сохранена.");
                string text=Store.Utf8.GetString(bytes).TrimStart('\ufeff');foreach(string line in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)){var match=Regex.Match(line,@"\A\s*([a-fA-F0-9]{64})(?=\s|$)");if(match.Success){if(Interlocked.Increment(ref rowCount)>100000)throw new IOException("Источник содержит больше 100000 IOC.");rows.Add(Store.Map("sha256",match.Groups[1].Value.ToLowerInvariant(),"name","Unit 42: "+path.Substring(0,Math.Min(path.Length,150)),"source",SourcePrefix+commit+"/"+Uri.EscapeDataString(path),"action","alert","kind","campaign-ioc"));}}
            });}catch(AggregateException error){cancel.ThrowIfCancellationRequested();throw new IOException("Обновление источника не завершено: "+error.Flatten().InnerExceptions[0].Message,error);}
            cancel.ThrowIfCancellationRequested();if(rows.Count==0)throw new IOException("В источнике нет допустимых SHA-256. Предыдущая база сохранена.");
            engine.MergePublishedFeed(rows,commit);return Checked(commit,true,files.Length);
        }
    }
}
