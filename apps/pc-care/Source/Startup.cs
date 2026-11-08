using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PCCare
{
    public sealed class RegistryLocation
    {
        public string Hive, Key;
        public RegistryView View;
    }
    public sealed class StartupEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Command { get; set; }
        public string Source { get; set; }
        public string Kind { get; set; }
        public string Hive { get; set; }
        public int View { get; set; }
        public string Key { get; set; }
        public int ValueKind { get; set; }
        public string OriginalPath { get; set; }
        public bool Enabled { get; set; }
        public bool? WindowsAllowed { get; set; }
        public string StatusKind { get { return !Enabled ? "Disabled" : !WindowsAllowed.HasValue ? "Unknown" : WindowsAllowed.Value ? "Enabled" : "Disabled"; } }
        public int StatusOrder { get { return StatusKind == "Enabled" ? 0 : StatusKind == "Disabled" ? 1 : 2; } }
        public string Status { get { return !Enabled ? "Выключено PC Care" : !WindowsAllowed.HasValue ? "Статус не определён" : WindowsAllowed.Value ? "Запускается" : "Выключено Windows"; } }
        public string StatusDetails { get; set; }
    }
    public sealed class StartupSnapshot
    {
        public List<StartupEntry> Entries = new List<StartupEntry>();
        public List<string> Errors = new List<string>();
    }
    public sealed class StartupManager
    {
        private readonly AppStore store;
        private readonly List<RegistryLocation> registry;
        private readonly List<string> folders;
        public StartupManager(AppStore store, IEnumerable<RegistryLocation> registry, IEnumerable<string> folders)
        {
            this.store = store; this.registry = registry.ToList(); this.folders = folders.Where(p => !string.IsNullOrEmpty(p)).Select(Paths.Full).ToList();
        }
        public static StartupManager Default(AppStore store)
        {
            const string key = @"Software\Microsoft\Windows\CurrentVersion\Run";
            var locations = new List<RegistryLocation> {
                new RegistryLocation { Hive = "HKCU", View = RegistryView.Default, Key = key },
                new RegistryLocation { Hive = "HKLM", View = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default, Key = key }
            };
            if (Environment.Is64BitOperatingSystem) locations.Add(new RegistryLocation { Hive = "HKLM", View = RegistryView.Registry32, Key = key });
            return new StartupManager(store, locations, new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
            });
        }
        private static RegistryKey BaseKey(string hive, int view)
        {
            if (hive != "HKCU" && hive != "HKLM") throw new InvalidDataException("Неизвестный раздел реестра.");
            return RegistryKey.OpenBaseKey(hive == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, (RegistryView)view);
        }
        private static string RegistryId(string hive, int view, string key, string name) { return Format.Id(hive + "|" + view + "|" + key + "|" + name); }
        public StartupSnapshot List()
        {
            var result = new StartupSnapshot();
            foreach (RegistryLocation location in registry)
            {
                try
                {
                    using (RegistryKey baseKey = BaseKey(location.Hive, (int)location.View))
                    using (RegistryKey key = baseKey.OpenSubKey(location.Key, false))
                    {
                        if (key == null) continue;
                        foreach (string name in key.GetValueNames())
                        {
                            RegistryValueKind kind = key.GetValueKind(name);
                            if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString) continue;
                            string value = (string)key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            var entry = new StartupEntry { Id = RegistryId(location.Hive, (int)location.View, location.Key, name), Name = name, Command = value, Source = location.Hive + (location.View == RegistryView.Registry32 ? " · 32 bit" : "") + " · Run", Kind = "Registry", Hive = location.Hive, View = (int)location.View, Key = location.Key, ValueKind = (int)kind, Enabled = true };
                            ApplyApproval(entry, location.Hive, location.View == RegistryView.Registry32 ? "Run32" : "Run");
                            result.Entries.Add(entry);
                        }
                    }
                }
                catch (UnauthorizedAccessException ex) { result.Errors.Add(location.Hive + ": " + ex.Message); }
                catch (System.Security.SecurityException ex) { result.Errors.Add(location.Hive + ": " + ex.Message); }
                catch (IOException ex) { result.Errors.Add(location.Hive + ": " + ex.Message); }
            }
            foreach (string folder in folders)
            {
                try
                {
                    if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (string path in Directory.EnumerateFiles(folder))
                    {
                        if (string.Equals(Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        var entry = new StartupEntry { Id = Format.Id(path), Name = Path.GetFileName(path), Command = path, Source = "Папка автозагрузки", Kind = "Folder", OriginalPath = path, Enabled = true };
                        ApplyApproval(entry, Paths.Same(folder, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)) ? "HKLM" : "HKCU", "StartupFolder");
                        result.Entries.Add(entry);
                    }
                }
                catch (UnauthorizedAccessException ex) { result.Errors.Add(folder + ": " + ex.Message); }
                catch (IOException ex) { result.Errors.Add(folder + ": " + ex.Message); }
            }
            foreach (StartupBackup backup in store.State.Backups.Where(b => b.State == "Disabled" || b.State == "Prepared"))
            {
                if (result.Entries.Any(e => e.Id == backup.Id)) continue;
                result.Entries.Add(new StartupEntry { Id = backup.Id, Name = backup.Name, Command = backup.Kind == "Folder" ? backup.OriginalPath : backup.Value, Source = "Резервная запись PC Care", Kind = backup.Kind, Hive = backup.Hive, View = backup.View, Key = backup.Key, OriginalPath = backup.OriginalPath, ValueKind = backup.ValueKind, Enabled = false });
            }
            result.Entries = result.Entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return result;
        }
        public static bool? DecodeApproval(byte[] bytes)
        {
            if (bytes == null) return true;
            if (bytes.Length < 12) return null;
            uint state = BitConverter.ToUInt32(bytes, 0);
            if (state > 7) return null;
            // The low bit is the Windows off flag; see Devolutions/UniGetUI, UniGetUI.iss IsAutostartDisabledByUser.
            return (state & 1) == 0;
        }
        private static void ApplyApproval(StartupEntry entry, string hive, string group)
        {
            const string approval = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
            try
            {
                using (RegistryKey baseKey = BaseKey(hive, (int)(Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default)))
                using (RegistryKey key = baseKey.OpenSubKey(approval + group, false))
                {
                    object raw = key == null ? null : key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    entry.WindowsAllowed = raw == null ? true : raw is byte[] ? DecodeApproval((byte[])raw) : null;
                    entry.StatusDetails = raw == null ? "Запись присутствует, дополнительный запрет StartupApproved не найден. Фактический запуск не отслеживается." : "Состояние Windows StartupApproved. Фактический запуск процесса не отслеживается.";
                }
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is System.Security.SecurityException)) throw;
                entry.WindowsAllowed = null; entry.StatusDetails = "Не удалось прочитать статус Windows: " + ex.Message;
            }
        }
        public void Disable(StartupEntry entry)
        {
            if (!entry.Enabled) throw new InvalidOperationException("Запись уже выключена.");
            if (store.State.Backups.Any(b => b.Id == entry.Id && b.State != "Restored")) throw new IOException("Есть незавершённая резервная запись. Сначала восстанови её через историю.");
            var backup = new StartupBackup { Id = entry.Id, Token = Guid.NewGuid().ToString("N"), Name = entry.Name, Kind = entry.Kind, Hive = entry.Hive, View = entry.View, Key = entry.Key, Value = entry.Command, ValueKind = entry.ValueKind, OriginalPath = entry.OriginalPath, State = "Prepared" };
            if (entry.Kind == "Folder")
            {
                CheckFolder(entry.OriginalPath);
                string directory = Path.Combine(store.Directory, "StartupBackups", entry.Id + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                backup.SavedPath = Path.Combine(directory, Path.GetFileName(entry.OriginalPath));
                File.Copy(entry.OriginalPath, backup.SavedPath, false);
                backup.Hash = Format.Hash(backup.SavedPath);
                if (backup.Hash != Format.Hash(entry.OriginalPath)) throw new IOException("Файл автозагрузки изменился во время копирования.");
            }
            Validate(backup);
            store.State.Backups.RemoveAll(b => b.Id == backup.Id);
            store.State.Backups.Add(backup);
            // Persist the original before mutating Windows; Prepared records are recoverable after a crash.
            store.Save();
            try
            {
                if (entry.Kind == "Registry")
                {
                    using (RegistryKey baseKey = BaseKey(entry.Hive, entry.View))
                    using (RegistryKey key = baseKey.OpenSubKey(entry.Key, true))
                    {
                        if (key == null || !Equals(key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), entry.Command) || (int)key.GetValueKind(entry.Name) != entry.ValueKind)
                            throw new IOException("Запись изменилась после чтения. Обнови список.");
                        key.DeleteValue(entry.Name, true);
                    }
                }
                else
                {
                    CheckFolder(entry.OriginalPath);
                    if (Format.Hash(entry.OriginalPath) != backup.Hash) throw new IOException("Файл автозагрузки изменился.");
                    File.Delete(entry.OriginalPath);
                }
            }
            catch
            {
                backup.State = "Restored"; store.Save(); throw;
            }
            backup.State = "Disabled"; store.Save();
            store.Record("Автозагрузка выключена", entry.Name, backup.Token);
        }
        public void Restore(string id)
        {
            StartupBackup backup = store.State.Backups.FirstOrDefault(b => (b.Id == id || b.Token == id) && (b.State == "Disabled" || b.State == "Prepared"));
            if (backup == null) throw new IOException("Доступная резервная запись не найдена.");
            Validate(backup);
            if (backup.Kind == "Registry")
            {
                using (RegistryKey baseKey = BaseKey(backup.Hive, backup.View))
                using (RegistryKey key = baseKey.CreateSubKey(backup.Key))
                {
                    if (key == null) throw new IOException("Не удалось открыть раздел реестра для восстановления.");
                    if (key.GetValueNames().Contains(backup.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!Equals(key.GetValue(backup.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), backup.Value) || (int)key.GetValueKind(backup.Name) != backup.ValueKind)
                            throw new IOException("На месте резервной записи уже есть другое значение. Оно сохранено без изменений.");
                    }
                    else key.SetValue(backup.Name, backup.Value, (RegistryValueKind)backup.ValueKind);
                }
            }
            else
            {
                string parent = Path.GetDirectoryName(backup.OriginalPath);
                if (!Directory.Exists(parent)) throw new IOException("Папка автозагрузки отсутствует.");
                if (File.Exists(backup.OriginalPath))
                {
                    CheckFolder(backup.OriginalPath);
                    if (Format.Hash(backup.OriginalPath) != backup.Hash) throw new IOException("В автозагрузке уже есть другой файл с этим именем.");
                }
                else
                {
                    if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка автозагрузки является ссылкой.");
                    if (!Paths.OrdinaryChain(backup.SavedPath, store.Directory) || Format.Hash(backup.SavedPath) != backup.Hash) throw new IOException("Резервный файл повреждён или перемещён.");
                    File.Copy(backup.SavedPath, backup.OriginalPath, false);
                }
            }
            backup.State = "Restored"; store.Save();
            store.Record("Автозагрузка восстановлена", backup.Name, null);
        }
        private void CheckFolder(string path)
        {
            string parent = Path.GetDirectoryName(Paths.Full(path));
            string folder = folders.FirstOrDefault(p => Paths.Same(p, parent));
            if (folder == null || !Paths.OrdinaryChain(path, folder)) throw new IOException("Файл вне обычной папки автозагрузки.");
        }
        private void Validate(StartupBackup backup)
        {
            if (backup.Kind == "Registry")
            {
                if (!registry.Any(r => r.Hive == backup.Hive && (int)r.View == backup.View && r.Key == backup.Key)) throw new IOException("Раздел реестра вне списка автозагрузки.");
                if (backup.ValueKind != (int)RegistryValueKind.String && backup.ValueKind != (int)RegistryValueKind.ExpandString) throw new IOException("Неизвестный тип значения реестра.");
                if (backup.Id != RegistryId(backup.Hive, backup.View, backup.Key, backup.Name)) throw new IOException("Неверный идентификатор резервной записи.");
            }
            else if (backup.Kind == "Folder")
            {
                if (backup.Id != Format.Id(backup.OriginalPath) || !folders.Any(p => Paths.Same(p, Path.GetDirectoryName(backup.OriginalPath)))) throw new IOException("Неверный путь автозагрузки.");
                if (!Paths.Within(backup.SavedPath, Path.Combine(store.Directory, "StartupBackups"))) throw new IOException("Неверный путь резервного файла.");
            }
            else throw new IOException("Неизвестный тип резервной записи.");
        }
    }
}
