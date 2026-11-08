using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace PCCare
{
    public class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Changed(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class Candidate : Observable
    {
        private bool selected;
        public string CategoryId { get; set; }
        public string Path { get; set; }
        public string Root { get; set; }
        public long Bytes { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public string Size { get { return Format.Bytes(Bytes); } }
        public string Modified { get { return ModifiedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"); } }
        public bool Selected { get { return selected; } set { if (selected != value) { selected = value; Changed("Selected"); } } }
    }

    public sealed class Category : Observable
    {
        private bool selected;
        public string Id { get; set; }
        public string Name { get; set; }
        public long Bytes { get; set; }
        public long Count { get; set; }
        public string Note { get; set; }
        public string Size { get { return Format.Bytes(Bytes); } }
        public bool Selected { get { return selected; } set { if (selected != value) { selected = value; Changed("Selected"); } } }
    }

    [DataContract]
    public sealed class Settings
    {
        [DataMember] public int MinimumAgeDays = 7;
        [DataMember] public int ScanFileLimit = 100000;
        [DataMember] public int ScanEntryLimit = 500000;
        [DataMember] public int ScanTimeLimitSeconds = 600;
        [DataMember] public int LargeFileMinimumMB = 500;
        [DataMember] public int LargeFileResultLimit = 1000;
        [DataMember] public string Theme = "Dark";
        public void Validate()
        {
            if (MinimumAgeDays < 1 || MinimumAgeDays > 365) throw new InvalidDataException("MinimumAgeDays: ожидается 1–365.");
            if (ScanFileLimit < 100 || ScanFileLimit > 1000000) throw new InvalidDataException("ScanFileLimit: ожидается 100–1000000.");
            if (ScanEntryLimit < ScanFileLimit || ScanEntryLimit > 5000000) throw new InvalidDataException("ScanEntryLimit: неверный предел.");
            if (ScanTimeLimitSeconds < 10 || ScanTimeLimitSeconds > 3600) throw new InvalidDataException("ScanTimeLimitSeconds: ожидается 10–3600.");
            if (LargeFileMinimumMB < 1 || LargeFileMinimumMB > 1000000) throw new InvalidDataException("LargeFileMinimumMB: неверный порог.");
            if (LargeFileResultLimit < 10 || LargeFileResultLimit > 10000) throw new InvalidDataException("LargeFileResultLimit: неверный предел.");
            if (Theme != "Dark" && Theme != "Light") Theme = "Dark";
        }
    }

    [DataContract]
    public sealed class HistoryEntry
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public DateTime TimeUtc = DateTime.UtcNow;
        [DataMember] public string Action;
        [DataMember] public string Details;
        [DataMember] public string BackupId;
        public string Time { get { return TimeUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"); } }
    }

    [DataContract]
    public sealed class StartupBackup
    {
        [DataMember] public string Id;
        [DataMember] public string Token;
        [DataMember] public string Name;
        [DataMember] public string Kind;
        [DataMember] public string Hive;
        [DataMember] public int View;
        [DataMember] public string Key;
        [DataMember] public string Value;
        [DataMember] public int ValueKind;
        [DataMember] public string OriginalPath;
        [DataMember] public string SavedPath;
        [DataMember] public string Hash;
        [DataMember] public string State;
    }

    [DataContract]
    public sealed class SavedState
    {
        [DataMember] public List<StartupBackup> Backups = new List<StartupBackup>();
        [DataMember] public List<HistoryEntry> History = new List<HistoryEntry>();
    }

    public sealed class AppStore
    {
        private readonly object gate = new object();
        public object SyncRoot { get { return gate; } }
        public readonly string Directory;
        public Settings Settings { get; private set; }
        public SavedState State { get; private set; }
        public AppStore(string directory)
        {
            Directory = System.IO.Path.GetFullPath(directory);
            System.IO.Directory.CreateDirectory(Directory);
            string settingsPath = System.IO.Path.Combine(Directory, "settings.json");
            Settings = Read<Settings>(settingsPath) ?? new Settings();
            Settings.Validate();
            if (!File.Exists(settingsPath)) Write(settingsPath, Settings);
            State = Read<SavedState>(System.IO.Path.Combine(Directory, "state.json")) ?? new SavedState();
            if (State.Backups == null || State.History == null) throw new InvalidDataException("Повреждён state.json: отсутствуют списки.");
        }
        public void Save()
        {
            lock (gate) Write(System.IO.Path.Combine(Directory, "state.json"), State);
        }
        public void SaveSettings()
        {
            lock (gate) { Settings.Validate(); Write(System.IO.Path.Combine(Directory, "settings.json"), Settings); }
        }
        public void Record(string action, string details, string backupId)
        {
            lock (gate)
            {
                State.History.Add(new HistoryEntry { Action = action, Details = details, BackupId = backupId });
                if (State.History.Count > 2000) State.History.RemoveRange(0, State.History.Count - 2000);
                Save();
            }
        }
        private static T Read<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Слишком большой файл настроек: " + path);
            using (FileStream stream = File.OpenRead(path))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        private static void Write<T>(string path, T value)
        {
            string temp = path + ".new";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
            else File.Move(temp, path);
        }
    }

    public static class Format
    {
        public static string Bytes(long value)
        {
            string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            double n = value; int unit = 0;
            while (n >= 1024 && unit < units.Length - 1) { n /= 1024; unit++; }
            return n.ToString(unit == 0 ? "0" : "0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " " + units[unit];
        }
        public static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
        }
        public static string Id(string value)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()))).Replace("-", "");
        }
    }

    public static class Paths
    {
        public static string Full(string path) { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        public static bool Within(string path, string root)
        {
            string p = Full(path), r = Full(root);
            return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        public static bool OrdinaryChain(string path, string root)
        {
            string p = Full(path), r = Full(root);
            if (!Within(p, r)) return false;
            while (true)
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
                p = Path.GetDirectoryName(p);
                if (string.IsNullOrEmpty(p)) return false;
            }
        }
        public static bool Same(string a, string b) { return string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase); }
    }
}
