using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCGuard
{
    // Режим чтения удерживает файловые дескрипторы: запись и удаление заблокированы для всех программ.
    // Это защита выбранных существующих файлов, а не идентификация шифровальщика или системный перехват.
    internal sealed class FolderProtection:IDisposable
    {
        private readonly Store store;private readonly SecureStorage storage;private readonly object gate=new object();
        private readonly Dictionary<string,FileStream> locks=new Dictionary<string,FileStream>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileSystemWatcher> watchers=new List<FileSystemWatcher>();
        private readonly Queue<DateTime> events=new Queue<DateTime>();private DateTime lastAlert=DateTime.MinValue;
        private Dictionary<string,object> manifest=Store.Map("enabled",false,"files",new object[0],"issues",new object[0]);
        private static readonly string[] extensions={".txt",".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx",".odt",".ods",".jpg",".jpeg",".png",".webp",".csv",".rtf",".sqlite",".db",".dat"};
        internal FolderProtection(Store store,SecureStorage storage){this.store=store;this.storage=storage;var saved=store.Read("folder-protection.json");if(saved.Count>0)manifest=saved;}
        internal object Status(){lock(gate)return Store.Map("manifest",manifest,"lockedFiles",locks.Count,"running",watchers.Count>0,"systemWideBlocking",false,"note","Блокировка записи относится ко всем программам. Для редактирования сними её. Работает, пока запущен PC Guard; не защищает от завершения PC Guard привилегированным процессом.");}
        internal object Protect(string root,bool lockWrites)
        {
            root=SafeFiles.Full(root);if(!Directory.Exists(root))throw new IOException("Папка не найдена.");SafeFiles.Ordinary(root);
            if(root.TrimEnd('\\').Length<4||SafeFiles.Within(store.Data,root)||SafeFiles.Within(root,store.Root)||SafeFiles.Within(root,Environment.GetFolderPath(Environment.SpecialFolder.Windows))||System.Text.RegularExpressions.Regex.IsMatch(root,@"(?i)\\IObit(?:\\|$)|\\Program Files(?: \(x86\))?(?:\\|$)|\\Windows Defender(?:\\|$)"))throw new IOException("Выбери отдельную папку документов вне системных данных, программ, IObit и PC Guard.");
            lock(gate)
            {
                Stop();var rows=new List<object>();var issues=new List<object>();var stack=new Stack<string>();stack.Push(root);long total=0;int visited=0;
                try
                {
                    while(stack.Count>0&&rows.Count<2000&&visited<20000)
                    {
                        string item=stack.Pop();visited++;
                        try
                        {
                            SafeFiles.Ordinary(item);if(Directory.Exists(item)){foreach(string child in Directory.EnumerateFileSystemEntries(item).Take(20001))stack.Push(child);continue;}
                            if(!extensions.Contains(Path.GetExtension(item).ToLowerInvariant())||Path.GetFileName(item).StartsWith(".pcguard-canary-",StringComparison.OrdinalIgnoreCase))continue;
                            FileStream handle=null;
                            try
                            {
                                handle=SafeFiles.OpenStable(item,false);if(handle.Length>64*1024*1024){issues.Add(Store.Map("path",item,"note","Файл больше 64 МиБ: резерв не создан"));continue;}
                                if(total+handle.Length>2L*1024*1024*1024){issues.Add(Store.Map("path",item,"note","Достигнут лимит резервов 2 ГиБ"));break;}
                                string hash=SafeFiles.Hash(handle);string backup=storage.Backup(item,"Recovery",hash);total+=handle.Length;
                                rows.Add(Store.Map("path",item,"sha256",hash,"size",handle.Length,"backup",backup));
                                if(lockWrites){locks.Add(item,handle);handle=null;}
                            }
                            finally{if(handle!=null)handle.Dispose();}
                        }
                        catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is System.ComponentModel.Win32Exception))throw;issues.Add(Store.Map("path",item,"note",error.Message));}
                    }
                    if(stack.Count>0)issues.Add(Store.Map("path",root,"note","Обход частичный: достигнут лимит числа объектов."));
                    string canary=Path.Combine(root,".pcguard-canary-"+Guid.NewGuid().ToString("N")+".txt");
                    File.WriteAllText(canary,"PC Guard: контрольный файл. Не изменяй его при обычной работе.",Store.Utf8);
                    manifest=Store.Map("enabled",true,"root",root,"lockWrites",lockWrites,"files",rows,"issues",issues,"canary",canary,"date",DateTimeOffset.Now.ToString("o"),"backupBytes",total);
                    store.Write("folder-protection.json",manifest);StartWatcher(root,canary);
                }
                catch{Stop();throw;}
                store.Journal("Защита папки","Выполнено","Созданы зашифрованные резервы: "+rows.Count+"; блокировка записи: "+lockWrites+"; пропуски: "+issues.Count);return Status();
            }
        }
        internal void Resume()
        {
            lock(gate)
            {
                if(!Object.Equals(Store.Get(manifest,"enabled"),true))return;
                string root=Store.Text(manifest,"root");if(!Directory.Exists(root))return;
                if(Object.Equals(Store.Get(manifest,"lockWrites"),true))foreach(var raw in Store.Array(Store.Get(manifest,"files")))
                {
                    var row=Store.Object(raw);string file=Store.Text(row,"path");
                    try{var handle=SafeFiles.OpenStable(file,false);locks.Add(file,handle);string hash=SafeFiles.Hash(handle);if(hash!=Store.Text(row,"sha256"))Alert("Файл изменился, пока защита не работала: "+file);}
                    catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is System.ComponentModel.Win32Exception))throw;Alert("Не удалось заблокировать файл: "+file+". "+error.Message);}
                }
                StartWatcher(root,Store.Text(manifest,"canary"));
            }
        }
        private void StartWatcher(string root,string canary)
        {
            var watcher=new FileSystemWatcher(root){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.Size,InternalBufferSize=32768};
            FileSystemEventHandler changed=delegate(object sender,FileSystemEventArgs args){Changed(args.FullPath,canary);};
            watcher.Changed+=changed;watcher.Deleted+=changed;watcher.Created+=changed;watcher.Renamed+=delegate(object sender,RenamedEventArgs args){Changed(args.OldFullPath,canary);};
            watcher.Error+=delegate(object sender,ErrorEventArgs args){Alert("Ошибка наблюдателя папки; события могли быть потеряны. "+args.GetException().Message);};
            watchers.Add(watcher);watcher.EnableRaisingEvents=true;
        }
        private void Changed(string path,string canary)
        {
            lock(gate)
            {
                if(path.Equals(canary,StringComparison.OrdinalIgnoreCase)){Alert("Изменён или удалён контрольный файл: "+path);return;}
                var now=DateTime.UtcNow;events.Enqueue(now);while(events.Count>0&&now-events.Peek()>TimeSpan.FromSeconds(5))events.Dequeue();
                if(events.Count>=40&&now-lastAlert>TimeSpan.FromSeconds(10)){lastAlert=now;Alert("Массовые изменения в наблюдаемой папке. Это сигнал для проверки, не подтверждённый вердикт о шифровальщике.");}
            }
        }
        private void Alert(string message)
        {
            lock(Store.Gate){var rows=Store.Array(Store.Get(store.Read("folder-alerts.json"),"rows"));rows.Add(Store.Map("date",DateTimeOffset.Now.ToString("o"),"message",message));store.Write("folder-alerts.json",Store.Map("rows",rows.Skip(Math.Max(0,rows.Count-500)).ToList()));}
            store.Journal("Защита папки","Внимание",message);
            Notifications.Send(message);
        }
        internal object Unlock()
        {
            lock(gate){Stop();manifest["enabled"]=false;store.Write("folder-protection.json",manifest);store.Journal("Защита папки","Выполнено","Блокировки сняты; зашифрованные резервы сохранены.");return Status();}
        }
        internal object Restore(string backup,string destination)
        {
            if(!Store.Array(Store.Get(manifest,"files")).Select(Store.Object).Any(r=>Store.Text(r,"backup")==backup))throw new IOException("Резерв отсутствует в текущем списке.");
            object result=storage.Restore(backup,"Recovery",destination);store.Journal("Восстановление документа","Выполнено",Store.Text(Store.Object(result),"path"));return result;
        }
        private void Stop(){foreach(var watcher in watchers){watcher.EnableRaisingEvents=false;watcher.Dispose();}watchers.Clear();foreach(var handle in locks.Values)handle.Dispose();locks.Clear();events.Clear();}
        public void Dispose(){lock(gate)Stop();}
    }
}
