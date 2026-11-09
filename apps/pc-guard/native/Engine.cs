using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PCGuard
{
    internal sealed class Engine
    {
        private readonly Store store; private readonly object gate = new object();
        private Dictionary<string, object> database;
        private Dictionary<string, Dictionary<string, object>> index;
        internal const string TestMarker = "PCGUARD-SAFE-SELFTEST-62B32842-2026";
        internal Engine(Store store)
        {
            this.store = store;
            string path = Path.Combine(store.Data, "engine-rules.json");
            if (!File.Exists(path)) Store.WritePath(path, Defaults());
            database = Validate(Store.Object(Store.ReadPath(path, null)));
            var installedSeed = store.Read("engine-seed-state.json");
            if(Store.Text(installedSeed,"version")!="2026.11.06-3")
            {
                // Встроенный выпуск дополняет локальные правила, сохраняя их при совпадении.
                var combined=Index(Validate(Defaults()));
                foreach(var row in Store.Array(Store.Get(database,"hashes")).Select(Store.Object))combined[Store.Text(row,"sha256")]=row;
                database=Validate(Store.Map("schema",1,"version","2026.11.06-3","hashes",combined.Values.ToArray()));
                store.Write("engine-rules.json",database);store.Write("engine-seed-state.json",Store.Map("version","2026.11.06-3"));
            }
            index = Index(database);
        }
        private static Dictionary<string, object> Defaults()
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("PCGuard.engine-seed.json"))
            {
                if(stream==null||stream.Length>16*1024*1024)throw new IOException("Встроенная база отсутствует или слишком велика.");
                using(var reader=new StreamReader(stream,Store.Utf8))return Store.Object(Store.Serializer().DeserializeObject(reader.ReadToEnd()));
            }
        }
        private static Dictionary<string, object> Validate(Dictionary<string, object> candidate)
        {
            if (Convert.ToInt32(Store.Get(candidate, "schema", 0)) != 1 || String.IsNullOrWhiteSpace(Store.Text(candidate, "version"))) throw new InvalidDataException("Неподдерживаемый формат базы.");
            var rows = Store.Array(Store.Get(candidate, "hashes")); if (rows.Count > 200000) throw new InvalidDataException("В базе больше 200000 записей.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in rows)
            {
                var row = Store.Object(raw); string hash = Store.Text(row, "sha256");
                if (!Regex.IsMatch(hash, @"\A[a-fA-F0-9]{64}\z") || !seen.Add(hash) || Store.Text(row, "name").Length == 0 || Store.Text(row, "name").Length > 200) throw new InvalidDataException("Некорректная или повторная SHA-256 запись.");
                row["sha256"] = hash.ToLowerInvariant();
                string action=Store.Text(row,"action");if(action.Length>0&&action!="block"&&action!="alert")throw new InvalidDataException("Неизвестное действие правила.");
            }
            return candidate;
        }
        internal object Info()
        {
            lock (gate) return Store.Map("version", Store.Get(database,"version"), "hashes", index.Count,"blockRules",index.Values.Count(r=>Store.Text(r,"action")!="alert"),"alertRules",index.Values.Count(r=>Store.Text(r,"action")=="alert"), "sha256", HashText(Store.Json(database)), "selftest", true, "heuristics", 3, "maxFileBytes", 536870912, "preExecutionBlocking", false, "note", "Исторические SHA-256 malware и индикаторы кампаний только для предупреждения. Отсутствие совпадения не гарантирует безопасность.");
        }
        private static string HashText(string text) { using (var input = new MemoryStream(Encoding.UTF8.GetBytes(text))) return SafeFiles.Hash(input); }
        private static Dictionary<string, Dictionary<string, object>> Index(Dictionary<string,object> value) { return Store.Array(Store.Get(value,"hashes")).Select(Store.Object).ToDictionary(r=>Store.Text(r,"sha256"),r=>r,StringComparer.OrdinalIgnoreCase); }
        internal object Import(string path, string expectedHash)
        {
            path = SafeFiles.Full(path); if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("База больше 16 МиБ.");
            if (!Regex.IsMatch(expectedHash ?? "", @"\A[a-fA-F0-9]{64}\z")) throw new ArgumentException("Укажи ожидаемую SHA-256 файла базы из доверенного источника.");
            byte[] bytes; using (var file = SafeFiles.OpenStable(path, false)) { using (var memory = new MemoryStream()) { file.CopyTo(memory); bytes = memory.ToArray(); } }
            string hash; using (var memory = new MemoryStream(bytes)) hash = SafeFiles.Hash(memory);
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("SHA-256 базы не совпала. Предыдущая база сохранена.");
            Dictionary<string,object> candidate;
            lock(gate) {
                if(String.Equals(Path.GetExtension(path),".json",StringComparison.OrdinalIgnoreCase)) candidate=Validate(Store.Object(Store.Serializer().DeserializeObject(Store.Utf8.GetString(bytes).TrimStart('\uFEFF'))));
                else {
                    var extension=Path.GetExtension(path).ToLowerInvariant();
                    if(extension!=".txt"&&extension!=".csv"&&extension!=".sha256")throw new InvalidDataException("Поддерживаются JSON PC Guard и списки SHA-256 TXT/CSV/SHA256; базы ClamAV CVD требуют отдельного движка.");
                    // Внешние списки дополняют базу предупреждениями, сохраняя существующие блокирующие правила.
                    var imported=HashListImport.Parse(Store.Utf8.GetString(bytes),Path.GetFileName(path));
                    var next=new Dictionary<string,Dictionary<string,object>>(index,StringComparer.OrdinalIgnoreCase);
                    foreach(var row in imported)if(!next.ContainsKey(Store.Text(row,"sha256")))next.Add(Store.Text(row,"sha256"),row);
                    candidate=Validate(Store.Map("schema",1,"version","Import-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"),"feedCommit",ManagedFeedCommit(),"hashes",next.Values.ToArray()));
                }
                if(Store.Utf8.GetByteCount(Store.Json(candidate))>16*1024*1024)throw new IOException("Объединённая база больше 16 МиБ. Предыдущая база сохранена.");
                store.Write("engine-rules.json",candidate);database=candidate;index=Index(candidate);
            }
            store.Journal("База собственного движка", "Выполнено", "Импорт версии " + Store.Text(candidate,"version") + "; проверен SHA-256 файла. Это контроль целостности, не подтверждение репутации издателя."); return Info();
        }
        internal string[] BlockHashes(){lock(gate)return index.Values.Where(r=>Store.Text(r,"action")!="alert").Select(r=>Store.Text(r,"sha256")).OrderBy(x=>x,StringComparer.Ordinal).ToArray();}
        internal string ManagedFeedCommit()
        {
            lock(gate){string value=Store.Text(database,"feedCommit");if(value.Length>0)return value;if(Store.Text(database,"version")!="2026.11.06-3")return "";string path=Path.Combine(store.Root,"rules","manifest.json");return File.Exists(path)?Store.Text(Store.Object(Store.ReadPath(path,null)),"currentCommit"):"";}
        }
        internal void MergePublishedFeed(IEnumerable<Dictionary<string,object>> published,string commit)
        {
            lock(gate)
            {
                var next=index.Values.Where(r=>!(Store.Text(r,"action")=="alert"&&Store.Text(r,"source").StartsWith(RuleUpdater.SourcePrefix,StringComparison.Ordinal))).ToDictionary(r=>Store.Text(r,"sha256"),r=>r,StringComparer.OrdinalIgnoreCase);
                foreach(var row in published)if(!next.ContainsKey(Store.Text(row,"sha256")))next.Add(Store.Text(row,"sha256"),row);
                var candidate=Validate(Store.Map("schema",1,"version","Unit42-"+commit.Substring(0,12),"feedCommit",commit,"hashes",next.Values.ToArray()));
                if(Store.Utf8.GetByteCount(Store.Json(candidate))>16*1024*1024)throw new IOException("Обновлённая база превышает 16 МиБ. Предыдущая база сохранена.");
                store.Write("engine-rules.json",candidate);database=candidate;index=Index(candidate);
            }
        }
        internal Dictionary<string, object> Scan(string path)
        {
            path = SafeFiles.Full(path); var reasons = new List<object>(); string hash = null; long size = 0;
            string verdict = "no-match", note = "Совпадений с текущей базой нет. Это не гарантия безопасности.";
            try
            {
                using (var file = SafeFiles.OpenStable(path, false))
                {
                    size = file.Length; if (size > 536870912) throw new IOException("Файл больше 512 МиБ: проверка пропущена.");
                    hash = SafeFiles.Hash(file); file.Position = 0;
                    Dictionary<string, object> match;
                    lock (gate) index.TryGetValue(hash,out match);
                    if (match != null) { verdict = Store.Text(match,"action")=="alert"?"published-ioc":"known-threat"; note = verdict=="published-ioc"?"Индикатор кампании: возможен легитимный инструмент. Автоматическая блокировка не применяется.":"Точное совпадение SHA-256 опубликованного malware."; reasons.Add(Store.Map("rule","sha256","name",Store.Get(match,"name"),"source",Store.Get(match,"source"))); }
                    // Анализ текста ограничен первыми 2 МиБ; бинарники не считаются безопасными по этому фрагменту.
                    byte[] prefix = new byte[(int)Math.Min(file.Length, 2 * 1024 * 1024)]; int read = 0, got;
                    while (read < prefix.Length && (got = file.Read(prefix,read,prefix.Length-read)) > 0) read += got;
                    string text = Encoding.UTF8.GetString(prefix,0,read);
                    if (file.Length<=128&&text.Trim().Equals(TestMarker,StringComparison.Ordinal)) { if (verdict != "known-threat") {verdict = "test-detection"; note = "Безопасная тестовая метка PC Guard; это не вредоносный файл.";} reasons.Add(Store.Map("rule","selftest","name","Тест канала обнаружения")); }
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (new[] {".ps1",".vbs",".js",".hta",".cmd",".bat"}.Contains(ext))
                    {
                        if (Regex.IsMatch(text,@"(?is)FromBase64String|\-enc(?:odedcommand)?\b") && Regex.IsMatch(text,@"(?is)\bIEX\b|Invoke-Expression|DownloadString|DownloadFile")) reasons.Add(Store.Map("rule","encoded-execution","name","Кодирование вместе с загрузкой или выполнением команд"));
                        if (Regex.IsMatch(text,@"(?is)vssadmin.{0,80}delete.{0,80}shadows|wbadmin.{0,80}delete.{0,80}catalog") && Regex.IsMatch(text,@"(?is)/quiet|Remove-Item|cipher|bcdedit")) reasons.Add(Store.Map("rule","recovery-destruction","name","Комбинация команд, затрудняющих восстановление"));
                        if (Regex.IsMatch(text,@"(?is)WScript\.Shell|ShellExecute") && Regex.IsMatch(text,@"(?is)https?://") && Regex.IsMatch(text,@"(?is)ADODB\.Stream|XMLHTTP")) reasons.Add(Store.Map("rule","script-dropper","name","Сценарий загружает данные и запускает команду"));
                        if (reasons.Count > 0 && verdict == "no-match") { verdict = "suspicious"; note = "Эвристическое предположение; требуется проверка. Автоматический карантин по этому признаку не выполняется."; }
                    }
                }
            }
            catch (Exception error)
            { if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.ComponentModel.Win32Exception)) throw; verdict = "not-scanned"; note = error.Message; }
            return Store.Map("id", Guid.NewGuid().ToString("N"), "path", path, "size", size, "sha256", hash, "verdict", verdict, "reasons", reasons, "note", note, "date", DateTimeOffset.Now.ToString("o"));
        }
        internal object ScanTree(string path, CancellationToken cancel)
        {
            path = SafeFiles.Full(path); var results = new List<object>(); var pending = new Stack<string>(); pending.Push(path); int visited = 0; bool partial = false;
            while (pending.Count > 0 && !cancel.IsCancellationRequested)
            {
                string item = pending.Pop(); if (++visited > 100000 || results.Count >= 10000) { partial = true; break; }
                try
                {
                    if (SafeFiles.Within(item,store.Data)) continue;
                    SafeFiles.Ordinary(item);
                    if (Directory.Exists(item)) foreach (string child in Directory.EnumerateFileSystemEntries(item).Take(100001)) pending.Push(child);
                    else if (File.Exists(item)) results.Add(Scan(item));
                }
                catch (Exception error)
                { if (!(error is IOException) && !(error is UnauthorizedAccessException)) throw; partial = true; results.Add(Store.Map("path",item,"verdict","not-scanned","note",error.Message,"date",DateTimeOffset.Now.ToString("o"))); }
            }
            return Store.Map("rows",results,"partial",partial||cancel.IsCancellationRequested,"cancelled",cancel.IsCancellationRequested,"count",results.Count,"note","Архивы и память процессов этим движком не распаковываются и не анализируются.");
        }
    }
}
