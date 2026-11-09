using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
namespace CreativeRelab.Updates
{
    public sealed class Product
    {
        public string Slug, Name; public bool Preview;
        public static Product Get(string slug)
        {
            switch(slug){case "pc-care":return new Product{Slug=slug,Name="PC Care",Preview=false};case "pc-guard":return new Product{Slug=slug,Name="PC Guard",Preview=true};case "pc-record":return new Product{Slug=slug,Name="PC Record",Preview=false};case "pc-tune":return new Product{Slug=slug,Name="PC Tune",Preview=true};default:throw new ArgumentException("Неизвестная программа.");}
        }
        public string MainExe { get { return Name+".exe"; } }
    }
    public sealed class Release
    {
        public Version Version; public string Tag, PageUrl, DownloadUrl, SHA256, FileName; public long Bytes; public bool Preview;
    }
    // Читаем только публичные выпуски собственного репозитория; подпись издателя не подменяется хешем.
    public static class ReleaseClient
    {
        public const string Repository="upe4d/creative-relab-software";
        public static string SafeFolder(string value)
        {
            var path=Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            if(!String.Equals(Path.GetPathRoot(path),@"D:\",StringComparison.OrdinalIgnoreCase)||path.Length<5)throw new IOException("Выберите отдельную папку на диске D.");
            for(var current=path;!String.IsNullOrEmpty(current)&&current.Length>3;current=Path.GetDirectoryName(current))
                if((Directory.Exists(current)||File.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Путь содержит перенаправление.");
            return path;
        }
        public static Version Normalize(string text)
        {
            Version v;if(!Version.TryParse(text,out v))throw new InvalidDataException("Не удалось прочитать номер версии.");
            return new Version(v.Major,v.Minor,Math.Max(0,v.Build),Math.Max(0,v.Revision));
        }
        public static Version Installed(Product product,string target)
        {
            target=SafeFolder(target);var path=Path.Combine(target,product.MainExe);
            if(!File.Exists(path))throw new FileNotFoundException("В выбранной папке нет "+product.MainExe);
            return Normalize(FileVersionInfo.GetVersionInfo(path).FileVersion);
        }
        private static Dictionary<string,object> Map(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
        private static object Value(Dictionary<string,object> value,string key,object fallback=null){object result;return value.TryGetValue(key,out result)?result:fallback;}
        private static string Text(Dictionary<string,object> value,string key){return Convert.ToString(Value(value,key))??"";}
        private static IEnumerable<object> Items(object value){return (value as System.Collections.IEnumerable??new object[0]).Cast<object>();}
        public static Release Parse(string json,Product product)
        {
            if(json==null||json.Length>4*1024*1024)throw new InvalidDataException("Ответ каталога слишком велик.");
            var rows=new JavaScriptSerializer{MaxJsonLength=4*1024*1024,RecursionLimit=32}.DeserializeObject(json) as object[];
            if(rows==null)throw new InvalidDataException("GitHub вернул некорректный список выпусков.");
            Dictionary<string,object> selected=null;Version latest=null;
            foreach(var item in rows.Take(100)){
                var row=Map(item);if(Convert.ToBoolean(Value(row,"draft",false))||(!product.Preview&&Convert.ToBoolean(Value(row,"prerelease",false))))continue;
                var tag=Text(row,"tag_name");if(!Regex.IsMatch(tag,"\\A"+Regex.Escape(product.Slug)+"-v[0-9]+\\.[0-9]+\\.[0-9]+\\z"))continue;
                var version=Normalize(tag.Substring(product.Slug.Length+2));if(latest==null||version>latest){selected=row;latest=version;}
            }
            if(selected==null)throw new InvalidDataException("Для этой программы пока нет подходящего выпуска.");
            var selectedTag=Text(selected,"tag_name");var plain=selectedTag.Substring(product.Slug.Length+2);var file=product.Slug+"-"+plain+"-portable.exe";
            var matches=Items(Value(selected,"assets")).Select(Map).Where(x=>Text(x,"name")==file).ToArray();
            if(matches.Length!=1)throw new InvalidDataException("Новый выпуск ещё не содержит готовый распаковщик. Повторите проверку позднее.");
            var asset=matches[0];var url=Text(asset,"browser_download_url");Uri uri;
            if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Host!="github.com"||uri.AbsolutePath!="/"+Repository+"/releases/download/"+selectedTag+"/"+file||uri.Query.Length!=0||uri.Fragment.Length!=0)throw new InvalidDataException("Неподходящий адрес загрузки.");
            var digest=Text(asset,"digest");if(!Regex.IsMatch(digest,"\\Asha256:[a-fA-F0-9]{64}\\z"))throw new InvalidDataException("GitHub не предоставил SHA-256 файла. Загрузка отменена.");
            long bytes;if(!Int64.TryParse(Text(asset,"size"),out bytes)||bytes<=0||bytes>512L*1024*1024)throw new InvalidDataException("Недопустимый размер распаковщика.");
            return new Release{Version=latest,Tag=selectedTag,PageUrl="https://github.com/"+Repository+"/releases/tag/"+selectedTag,DownloadUrl=url,FileName=file,SHA256=digest.Substring(7),Bytes=bytes,Preview=Convert.ToBoolean(Value(selected,"prerelease",false))};
        }
        private static HttpClient Client()
        {
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=true,MaxAutomaticRedirections=5}){Timeout=TimeSpan.FromMinutes(10),MaxResponseContentBufferSize=4*1024*1024};
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CreativeRelabUpdates/1.0");return client;
        }
        public static async Task<Release> Latest(Product product,CancellationToken token=default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            using(var client=Client())using(var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token)){
                cancellation.CancelAfter(TimeSpan.FromSeconds(25));
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using(var response=await client.GetAsync("https://api.github.com/repos/"+Repository+"/releases?per_page=50",cancellation.Token).ConfigureAwait(false)){
                    response.EnsureSuccessStatusCode();if(response.RequestMessage.RequestUri.Host!="api.github.com")throw new IOException("GitHub перенаправил запрос на другой сервер.");
                    return Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false),product);
                }
            }
        }
        public static bool VerifyFile(string path,string expected,long bytes)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var sha=SHA256.Create())
                return stream.Length==bytes&&String.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",""),expected,StringComparison.OrdinalIgnoreCase);
        }
        public static async Task<string> Download(Release release,Product product,Action<int> progress,CancellationToken token=default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            var folder=SafeFolder(Path.Combine(@"D:\Codex\downloads",product.Slug,release.Version.ToString(3),DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8)));
            Directory.CreateDirectory(folder);var partial=Path.Combine(folder,release.FileName+".partial");var destination=Path.Combine(folder,release.FileName);
            using(var client=Client())using(var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token)){
                cancellation.CancelAfter(TimeSpan.FromMinutes(10));
                using(var response=await client.GetAsync(release.DownloadUrl,HttpCompletionOption.ResponseHeadersRead,cancellation.Token).ConfigureAwait(false)){
                response.EnsureSuccessStatusCode();var final=response.RequestMessage.RequestUri;
                if(final.Scheme!="https"||!new[]{"github.com","release-assets.githubusercontent.com","objects.githubusercontent.com","github-releases.githubusercontent.com"}.Contains(final.Host))throw new IOException("Неожиданное перенаправление загрузки.");
                if(response.Content.Headers.ContentLength.HasValue&&response.Content.Headers.ContentLength.Value!=release.Bytes)throw new IOException("Размер ответа не совпал с каталогом.");
                using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)){
                    var buffer=new byte[65536];long total=0;int read;
                    while((read=await input.ReadAsync(buffer,0,buffer.Length,cancellation.Token).ConfigureAwait(false))>0){total+=read;if(total>release.Bytes)throw new IOException("Файл превысил заявленный размер.");await output.WriteAsync(buffer,0,read,cancellation.Token).ConfigureAwait(false);progress((int)(total*100/release.Bytes));}
                    if(total!=release.Bytes)throw new IOException("Загрузка не завершена. Частичный файл сохранён на D.");
                }
            }
            }
            if(!VerifyFile(partial,release.SHA256,release.Bytes))throw new IOException("SHA-256 не совпала. Распаковщик не будет запущен; частичный файл сохранён на D.");
            File.Move(partial,destination);return destination;
        }
    }
}
