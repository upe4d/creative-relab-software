using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace PCCare
{
    // Снимок хранит исходный тип и байтовое представление, без раскрытия REG_EXPAND_SZ.
    [DataContract] public sealed class RegistryValueSnapshot
    {
        [DataMember] public RegistryValueKind Kind;
        [DataMember] public string Data;
        public static RegistryValueSnapshot Capture(RegistryKey key, string name)
        {
            if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
            RegistryValueKind kind = key.GetValueKind(name);
            object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            byte[] bytes;
            switch (kind)
            {
                case RegistryValueKind.String: case RegistryValueKind.ExpandString: bytes = Encoding.Unicode.GetBytes((string)value); break;
                case RegistryValueKind.MultiString: bytes = Encode((string[])value); break;
                case RegistryValueKind.DWord: bytes = BitConverter.GetBytes((int)value); break;
                case RegistryValueKind.QWord: bytes = BitConverter.GetBytes((long)value); break;
                case RegistryValueKind.Binary: case RegistryValueKind.None: bytes = (byte[])value; break;
                default: throw new InvalidDataException("Неподдерживаемый тип значения реестра");
            }
            return new RegistryValueSnapshot { Kind = kind, Data = Convert.ToBase64String(bytes) };
        }
        private static byte[] Encode(string[] values) { using (var m = new MemoryStream()) { new DataContractJsonSerializer(typeof(string[])).WriteObject(m, values); return m.ToArray(); } }
        public object Value()
        {
            byte[] b = Convert.FromBase64String(Data);
            switch (Kind)
            {
                case RegistryValueKind.String: case RegistryValueKind.ExpandString: return Encoding.Unicode.GetString(b);
                case RegistryValueKind.MultiString: using (var m = new MemoryStream(b)) return new DataContractJsonSerializer(typeof(string[])).ReadObject(m);
                case RegistryValueKind.DWord: if (b.Length != 4) throw new InvalidDataException(); return BitConverter.ToInt32(b, 0);
                case RegistryValueKind.QWord: if (b.Length != 8) throw new InvalidDataException(); return BitConverter.ToInt64(b, 0);
                case RegistryValueKind.Binary: case RegistryValueKind.None: return b;
                default: throw new InvalidDataException("Неизвестный тип резервного значения");
            }
        }
        public bool Same(RegistryValueSnapshot other) { return other != null && Kind == other.Kind && Data == other.Data; }
    }
    [DataContract] public sealed class RegistryFinding
    {
        [DataMember] public string KeyPath { get; set; }
        [DataMember] public string ValueName { get; set; }
        [DataMember] public string Category { get; set; }
        [DataMember] public string Target { get; set; }
        [DataMember] public string Reason { get; set; }
        [DataMember] public bool Eligible { get; set; }
        [DataMember] public RegistryValueSnapshot Original;
        [IgnoreDataMember] public bool Selected { get; set; }
        public string Location { get { return "HKCU\\" + KeyPath + " :: " + (ValueName == "" ? "(по умолчанию)" : ValueName); } }
        public string Status { get { return Eligible ? "Можно исправить" : "Требует проверки"; } }
    }
    [DataContract] public sealed class RegistryBackupItem
    {
        [DataMember] public RegistryFinding Finding;
        [DataMember] public string State = "Prepared";
        [DataMember] public string Detail = "";
    }
    [DataContract] public sealed class RegistryBackup
    {
        [DataMember] public string Id;
        [DataMember] public string Created;
        [DataMember] public int View;
        [DataMember] public List<RegistryBackupItem> Items = new List<RegistryBackupItem>();
        public string Path { get; set; }
        public string Summary { get { return Items.Count(i => i.State == "Removed").ToString() + " исправлено / " + Items.Count(i => i.State == "Restored") + " восстановлено"; } }
    }
    [DataContract] internal sealed class RegistryEnvelope { [DataMember] public string Payload; [DataMember] public string Sha256; }
    public sealed class RegistryAudit
    {
        private static readonly object Gate = new object();
        private readonly string storage;
        private readonly string root;
        private readonly RegistryView view;
        public const string TestPrefix = @"Software\CreativeRelab\PC Care\Tests\RegistryAudit\";
        public RegistryAudit(string storage) : this(storage, @"Software\Microsoft\Windows\CurrentVersion", Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32) { }
        internal RegistryAudit(string storage, string root, RegistryView view)
        {
            if (root != @"Software\Microsoft\Windows\CurrentVersion" && !(root.StartsWith(TestPrefix, StringComparison.Ordinal) && root.Length > TestPrefix.Length)) throw new ArgumentException("Недопустимая область реестра");
            this.storage = System.IO.Path.GetFullPath(storage); this.root = root; this.view = view; Directory.CreateDirectory(this.storage);
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFileAttributesW(string path);
        // File.Exists скрывает отказ доступа; здесь отсутствие и недоступность различаются.
        public static string MissingReason(string path)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path) || path.IndexOf('%') >= 0 || path.IndexOfAny(new[] { '*', '?', '"', '\0' }) >= 0 || !System.IO.Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return "Путь нельзя однозначно проверить";
                path = System.IO.Path.GetFullPath(path);
                var drive = new DriveInfo(System.IO.Path.GetPathRoot(path));
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return "Съёмный, сетевой или недоступный диск";
                uint a = GetFileAttributesW(path);
                if (a != UInt32.MaxValue) return null;
                int error = Marshal.GetLastWin32Error();
                if (error != 2 && error != 3) return "Путь недоступен: Win32 " + error;
                string parent = System.IO.Path.GetDirectoryName(path);
                while (!String.IsNullOrEmpty(parent))
                {
                    a = GetFileAttributesW(parent);
                    if (a != UInt32.MaxValue) return (a & 0x400) != 0 ? "Родительская папка является ссылкой" : "Файл отсутствует";
                    error = Marshal.GetLastWin32Error();
                    if (error != 2 && error != 3) return "Родительская папка недоступна: Win32 " + error;
                    parent = System.IO.Path.GetDirectoryName(parent);
                }
                return "Не удалось проверить родительскую папку";
            }
            catch (Exception ex) { return "Путь не проверен: " + ex.GetType().Name; }
        }
        public static string Executable(string text, bool command)
        {
            if (text == null) return null;
            string s = Environment.ExpandEnvironmentVariables(text.Trim());
            if (s.StartsWith("\"", StringComparison.Ordinal)) { int end = s.IndexOf('"', 1); if (end < 0 || (!command && s.Substring(end + 1).Trim().Length != 0)) return null; return s.Substring(1, end - 1); }
            if (command && s.IndexOfAny(new[] { ' ', '\t' }) >= 0) return null;
            return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : null;
        }
        private string MuiKey { get { return root.StartsWith(TestPrefix, StringComparison.Ordinal) ? root + @"\MuiCache" : @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"; } }
        private bool Allowed(RegistryFinding f)
        {
            if (f == null || f.Original == null || f.KeyPath == null || f.ValueName == null) return false;
            if (f.KeyPath.Equals(root + @"\Run", StringComparison.OrdinalIgnoreCase)) return f.Category == "Автозагрузка" && f.ValueName.Length > 0;
            string prefix = root + @"\App Paths\";
            if (f.KeyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.KeyPath.Substring(prefix.Length).IndexOf('\\') < 0) return f.ValueName == "" && f.Category == "Пути приложений";
            return f.KeyPath.Equals(MuiKey, StringComparison.OrdinalIgnoreCase) && f.Category == "Кэш названий" && (f.ValueName.EndsWith(".FriendlyAppName", StringComparison.OrdinalIgnoreCase) || f.ValueName.EndsWith(".ApplicationCompany", StringComparison.OrdinalIgnoreCase));
        }
        private static string TargetOf(RegistryFinding f)
        {
            if (f.Category == "Кэш названий") return f.ValueName.Substring(0, f.ValueName.LastIndexOf('.'));
            return (f.Original.Kind == RegistryValueKind.String || f.Original.Kind == RegistryValueKind.ExpandString) ? Executable((string)f.Original.Value(), f.Category == "Автозагрузка") : null;
        }
        public List<RegistryFinding> Scan(CancellationToken token)
        {
            var rows = new List<RegistryFinding>();
            using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
            {
                ScanValues(hive, root + @"\Run", "Автозагрузка", rows, token, false);
                using (RegistryKey app = hive.OpenSubKey(root + @"\App Paths"))
                {
                    if (app != null) foreach (string name in app.GetSubKeyNames().Take(5000)) { token.ThrowIfCancellationRequested(); ScanValues(hive, root + @"\App Paths\" + name, "Пути приложений", rows, token, true); }
                }
                ScanValues(hive, MuiKey, "Кэш названий", rows, token, false);
            }
            return rows;
        }
        private void ScanValues(RegistryKey hive, string path, string category, List<RegistryFinding> rows, CancellationToken token, bool defaultOnly)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(path))
                {
                    if (key == null) return;
                    foreach (string name in key.GetValueNames().Take(20000))
                    {
                        token.ThrowIfCancellationRequested(); if (defaultOnly && name != "") continue;
                        var f = new RegistryFinding { KeyPath = path, ValueName = name, Category = category, Original = RegistryValueSnapshot.Capture(key, name) };
                        if (!Allowed(f)) continue;
                        f.Target = TargetOf(f); f.Reason = MissingReason(f.Target);
                        if (f.Reason == null) continue;
                        f.Eligible = f.Reason == "Файл отсутствует"; rows.Add(f);
                    }
                }
            }
            catch (System.Security.SecurityException) { rows.Add(new RegistryFinding { KeyPath = path, ValueName = "", Category = category, Reason = "Нет доступа к разделу" }); }
            catch (UnauthorizedAccessException) { rows.Add(new RegistryFinding { KeyPath = path, ValueName = "", Category = category, Reason = "Нет доступа к разделу" }); }
        }
        private static string Digest(byte[] b) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", ""); }
        internal static byte[] Serialize<T>(T obj) { using (var m = new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(m, obj); return m.ToArray(); } }
        internal static T Deserialize<T>(byte[] b) { using (var m = new MemoryStream(b)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(m); }
        private void Save(RegistryBackup b)
        {
            byte[] data = Serialize(b);
            byte[] bytes = Serialize(new RegistryEnvelope { Payload = Convert.ToBase64String(data), Sha256 = Digest(data) });
            string temp = b.Path + ".new";
            using (var s = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { s.Write(bytes, 0, bytes.Length); s.Flush(true); }
            if (File.Exists(b.Path)) File.Replace(temp, b.Path, b.Path + ".previous", true); else File.Move(temp, b.Path);
        }
        public RegistryBackup Load(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            if (!path.StartsWith(storage.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetExtension(path) != ".json" || new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Недопустимая резервная копия");
            RegistryEnvelope e = Deserialize<RegistryEnvelope>(File.ReadAllBytes(path));
            byte[] data = Convert.FromBase64String(e.Payload);
            if (Digest(data) != e.Sha256) throw new InvalidDataException("Контрольная сумма резервной копии не совпадает");
            RegistryBackup b = Deserialize<RegistryBackup>(data);
            if (b.View != (int)view || b.Items == null || b.Items.Count > 20000 || b.Items.Any(i => i == null || !Allowed(i.Finding))) throw new InvalidDataException("Область резервной копии не совпадает");
            b.Path = path; return b;
        }
        public List<RegistryBackup> Backups()
        {
            var result = new List<RegistryBackup>();
            foreach (string p in Directory.GetFiles(storage, "*.json").OrderByDescending(p => p).Take(500)) { try { result.Add(Load(p)); } catch (Exception) { /* Повреждённая копия остаётся на диске, но не предлагается для отката. */ } }
            return result;
        }
        public RegistryBackup Fix(IEnumerable<RegistryFinding> selected, CancellationToken token)
        {
            lock (Gate)
            {
                var b = new RegistryBackup { Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"), Created = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"), View = (int)view };
                foreach (RegistryFinding f in selected.Take(20001))
                {
                    if (!Allowed(f) || !f.Eligible || f.Target != TargetOf(f)) throw new InvalidDataException("Некорректная находка");
                    if (!b.Items.Any(i => i.Finding.KeyPath.Equals(f.KeyPath, StringComparison.OrdinalIgnoreCase) && i.Finding.ValueName.Equals(f.ValueName, StringComparison.OrdinalIgnoreCase))) b.Items.Add(new RegistryBackupItem { Finding = f });
                }
                if (b.Items.Count == 0 || b.Items.Count > 20000) throw new InvalidDataException("Нужно выбрать от 1 до 20000 записей");
                b.Path = System.IO.Path.Combine(storage, b.Id + ".json"); Save(b);
                using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                foreach (RegistryBackupItem item in b.Items)
                {
                    if (token.IsCancellationRequested) break;
                    RegistryFinding f = item.Finding;
                    try
                    {
                        using (RegistryKey key = hive.OpenSubKey(f.KeyPath, true))
                        {
                            if (key == null || !f.Original.Same(RegistryValueSnapshot.Capture(key, f.ValueName)) || MissingReason(TargetOf(f)) != "Файл отсутствует") { item.State = "Skipped"; item.Detail = "Запись или доступность файла изменились"; }
                            else { key.DeleteValue(f.ValueName, true); key.Flush(); item.State = "Removed"; }
                        }
                    }
                    catch (Exception ex) { item.State = "Failed"; item.Detail = ex.Message; }
                    Save(b);
                }
                return b;
            }
        }
        public RegistryBackup Restore(string path, CancellationToken token)
        {
            lock (Gate)
            {
                RegistryBackup b = Load(path);
                using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                foreach (RegistryBackupItem item in b.Items)
                {
                    if (token.IsCancellationRequested) break;
                    if (item.State != "Removed" && item.State != "Prepared" && item.State != "Conflict") continue;
                    try
                    {
                        using (RegistryKey key = hive.CreateSubKey(item.Finding.KeyPath))
                        {
                            RegistryValueSnapshot current = RegistryValueSnapshot.Capture(key, item.Finding.ValueName);
                            if (current != null && !item.Finding.Original.Same(current)) { item.State = "Conflict"; item.Detail = "Другая запись сохранена; перезаписи нет"; }
                            else { if (current == null) key.SetValue(item.Finding.ValueName, item.Finding.Original.Value(), item.Finding.Original.Kind); key.Flush(); item.State = "Restored"; item.Detail = ""; }
                        }
                    }
                    catch (Exception ex) { item.Detail = "Ошибка восстановления: " + ex.Message; }
                    Save(b);
                }
                return b;
            }
        }
        public string Report(List<RegistryFinding> rows)
        {
            string path = System.IO.Path.Combine(storage, "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".html");
            Func<string, string> esc = s => System.Security.SecurityElement.Escape(s ?? "");
            var html = new StringBuilder("<!doctype html><html lang=ru><meta charset=utf-8><title>PC Care · Анализ реестра</title><style>body{font:15px Segoe UI;margin:32px;color:#172333}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccd4df;padding:10px;text-align:left;overflow-wrap:anywhere}</style><h1>Анализ реестра PC Care</h1><p>" + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " · HKCU · записей: " + rows.Count + "</p><p>Отчёт не означает, что все найденные записи нужно удалять. Исправление выполняется только по выбору.</p><table><tr><th>Категория</th><th>Реестр</th><th>Путь</th><th>Результат</th></tr>");
            foreach (RegistryFinding f in rows) html.Append("<tr><td>" + esc(f.Category) + "</td><td>" + esc(f.Location) + "</td><td>" + esc(f.Target) + "</td><td>" + esc(f.Reason) + "</td></tr>");
            html.Append("</table></html>"); File.WriteAllText(path, html.ToString(), new UTF8Encoding(true)); return path;
        }
    }
}
