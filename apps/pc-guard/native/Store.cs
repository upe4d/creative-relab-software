using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PCGuard
{
    // JSON, журнал и настройки отделены от HTTP и системных операций.
    internal sealed class Store
    {
        internal static readonly object Gate = new object();
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal readonly string Root, Data;
        internal Store(string root)
        {
            Root = root; Data = Path.Combine(root, "Data");
            foreach (string suffix in new[] { "", "Jobs", "Backups", "Reports", "Snapshots", "Temp", "Tests" }) Directory.CreateDirectory(Path.Combine(Data, suffix));
        }
        internal static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 64 }; }
        internal static string Json(object value) { return Serializer().Serialize(value); }
        internal static Dictionary<string, object> Object(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        internal static List<object> Array(object value) { var array = value as IEnumerable; return value is string || array == null ? new List<object>() : array.Cast<object>().ToList(); }
        internal static object Get(Dictionary<string, object> data, string key, object fallback = null) { object value; return data.TryGetValue(key, out value) ? value : fallback; }
        internal static string Text(Dictionary<string, object> data, string key) { return Convert.ToString(Get(data, key)) ?? ""; }
        internal static Dictionary<string, object> Map(params object[] pairs)
        {
            var result = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]); return result;
        }
        internal static string ReadText(string path)
        {
            for(int attempt=0;;attempt++)
            {
                try{return ReadTextOnce(path);}
                catch(IOException error){int code=error.HResult&0xffff;if(attempt>=6||(code!=32&&code!=33))throw;System.Threading.Thread.Sleep(10<<attempt);}
            }
        }
        private static string ReadTextOnce(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 16 * 1024 * 1024) throw new IOException("Файл данных слишком велик.");
                using (var reader = new StreamReader(stream, Utf8, true)) return reader.ReadToEnd();
            }
        }
        internal static object ReadPath(string path, object fallback)
        {
            return File.Exists(path) ? Serializer().DeserializeObject(ReadText(path)) : fallback;
        }
        internal static void WritePath(string path, object value)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, Json(value), Utf8);
            // Краткая внешняя блокировка не должна терять результат задания; другие ошибки не скрываются.
            for(int attempt=0;;attempt++)
            {
                try{if(File.Exists(path))File.Replace(temporary,path,path+".bak");else File.Move(temporary,path);return;}
                catch(IOException error){int code=error.HResult&0xffff;if(attempt>=6||(code!=32&&code!=33))throw;System.Threading.Thread.Sleep(10<<attempt);}
            }
        }
        private string Name(string name)
        {
            if (!Regex.IsMatch(name, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,100}\.json\z")) throw new ArgumentException("Недопустимое имя данных.");
            return Path.Combine(Data, name);
        }
        internal Dictionary<string, object> Read(string name) { lock (Gate) return Object(ReadPath(Name(name), new Dictionary<string, object>())); }
        internal void Write(string name, object value) { lock (Gate) WritePath(Name(name), value); }
        internal List<object> History()
        {
            lock (Gate)
            {
                string path = Path.Combine(Data, "journal.jsonl"); var result = new List<object>();
                if (!File.Exists(path)) return result;
                foreach (string line in ReadText(path).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try { result.Add(Serializer().DeserializeObject(line)); } catch (ArgumentException) { }
                }
                return result.Skip(Math.Max(0, result.Count - 2000)).Reverse().ToList();
            }
        }
        internal bool Journal(string action, string status, string detail, string job = null)
        {
            lock (Gate)
            {
                string marker = job == null ? null : Path.Combine(Data, "Jobs", job + ".logged");
                if (marker != null && File.Exists(marker)) return false;
                string path = Path.Combine(Data, "journal.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 8 * 1024 * 1024)
                {
                    string[] lines = ReadText(path).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    File.WriteAllText(path, String.Join("\n", lines.Skip(Math.Max(0, lines.Length - 2000))) + "\n", Utf8);
                }
                if (detail.Length > 2000) detail = detail.Substring(0, 2000);
                File.AppendAllText(path, Json(Map("date", DateTimeOffset.Now.ToString("o"), "action", action, "status", status, "detail", detail, "job", job)) + "\n", Utf8);
                if (marker != null) File.WriteAllText(marker, "1", Utf8);
                return true;
            }
        }
    }
}
