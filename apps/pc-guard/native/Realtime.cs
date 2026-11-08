using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCGuard
{
    // Watcher уведомляет после файлового события. Перехвата исполнения этот модуль не обещает.
    internal sealed class Realtime:IDisposable
    {
        private readonly Store store;private readonly Engine engine;private readonly SecureStorage storage;
        private readonly List<FileSystemWatcher> watchers=new List<FileSystemWatcher>();
        private readonly ConcurrentDictionary<string,DateTime> pending=new ConcurrentDictionary<string,DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,string> fingerprints=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate=new object();private readonly Timer timer;private bool enabled,quarantine,busy,disposed;
        private List<string> roots=new List<string>();private DateTime lastFull=DateTime.MinValue;private int errors;private bool overflowReported;
        internal Realtime(Store store,Engine engine,SecureStorage storage)
        {
            this.store=store;this.engine=engine;this.storage=storage;timer=new Timer(Tick,null,1000,1000);
        }
        internal object Status(){lock(gate)return Store.Map("enabled",enabled,"roots",roots.ToArray(),"quarantine",quarantine,"pending",pending.Count,"busy",busy,"errors",errors,"preExecutionBlocking",false,"mode","Файловое наблюдение после изменения; без перехвата исполнения");}
        internal object Configure(IEnumerable<string> paths,bool turnOn,bool autoQuarantine)
        {
            var next=paths.Select(SafeFiles.Full).Distinct(StringComparer.OrdinalIgnoreCase).ToList();if(next.Count>16)throw new ArgumentException("Не больше 16 папок наблюдения.");
            foreach(string path in next){if(!Directory.Exists(path))throw new IOException("Папка не найдена: "+path);SafeFiles.Ordinary(path);if(SafeFiles.Within(store.Data,path)||SafeFiles.Within(path,store.Data)||path.TrimEnd('\\').Length<4)throw new IOException("Нельзя наблюдать весь диск или данные PC Guard.");}
            if(turnOn&&next.Count==0)throw new ArgumentException("Добавь папку наблюдения.");
            lock(gate)
            {
                StopWatchers();roots=next;enabled=turnOn;quarantine=autoQuarantine;pending.Clear();fingerprints.Clear();
                if(enabled)foreach(string path in roots)
                {
                    var watcher=new FileSystemWatcher(path){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.Size,InternalBufferSize=32768};
                    watcher.Created+=Changed;watcher.Changed+=Changed;watcher.Renamed+=Renamed;watcher.Error+=Error;watchers.Add(watcher);watcher.EnableRaisingEvents=true;
                }
                store.Write("realtime.json",Store.Map("enabled",enabled,"roots",roots.ToArray(),"quarantine",quarantine));lastFull=DateTime.MinValue;
            }
            store.Journal("Фоновый контроль", "Выполнено",turnOn?"Наблюдение включено. Проверка выполняется после файлового события.":"Наблюдение выключено.");return Status();
        }
        private void Changed(object sender,FileSystemEventArgs args){Queue(args.FullPath);}
        private void Renamed(object sender,RenamedEventArgs args){Queue(args.FullPath);}
        private void Queue(string path,bool periodic=false)
        {
            lock(gate){if(!enabled||disposed)return;try{SafeFiles.Full(path);if(Directory.Exists(path)){lastFull=DateTime.MinValue;return;}if(periodic&&File.Exists(path)){var info=new FileInfo(path);string previous;if(fingerprints.TryGetValue(path,out previous)&&previous==info.Length+"|"+info.LastWriteTimeUtc.Ticks)return;}if(pending.Count>=4096){errors++;if(!overflowReported){overflowReported=true;store.Journal("Фоновый контроль","Ошибка","Очередь заполнена: часть файловых событий потеряна. Покрытие неполное.");}return;}if(pending.Count<2048)overflowReported=false;if(roots.Any(r=>SafeFiles.Within(path,r)))pending[path]=DateTime.UtcNow.AddMilliseconds(700);}catch(IOException){errors++;}catch(UnauthorizedAccessException){errors++;}}
        }
        private void Error(object sender,ErrorEventArgs args)
        {
            lock(gate){errors++;lastFull=DateTime.MinValue;}store.Journal("Фоновый контроль","Ошибка","Переполнение или ошибка наблюдателя. Выполнится ограниченный повторный обход: "+args.GetException().Message);
        }
        private void Tick(object ignored)
        {
            lock(gate){if(!enabled||busy||disposed)return;busy=true;}
            try
            {
                List<string> current;bool periodic;
                lock(gate){current=roots.ToList();periodic=DateTime.UtcNow-lastFull>TimeSpan.FromMinutes(5);if(periodic)lastFull=DateTime.UtcNow;}
                if(periodic)
                {
                    int count=0;var directories=new Stack<string>(current);
                    while(directories.Count>0&&count<20000)
                    {
                        string directory=directories.Pop();try{SafeFiles.Ordinary(directory);foreach(string child in Directory.EnumerateFileSystemEntries(directory)){if(++count>20000)break;if((File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)continue;if(Directory.Exists(child))directories.Push(child);else Queue(child,true);}}
                        catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException))throw;lock(gate)errors++;}
                    }
                    if(count>=20000)store.Journal("Фоновый контроль","Ошибка","Достигнут предел повторного обхода 20000 объектов. Покрытие частичное.");
                }
                foreach(var item in pending.Where(p=>p.Value<=DateTime.UtcNow).Take(64).ToArray())
                {
                    DateTime due;if(!pending.TryRemove(item.Key,out due)||!File.Exists(item.Key))continue;
                    try
                    {
                        var file=new FileInfo(item.Key);string stamp=file.Length+"|"+file.LastWriteTimeUtc.Ticks;
                        if(file.Length>64*1024*1024){lock(gate)errors++;Record(Store.Map("path",item.Key,"verdict","not-scanned","note","Лимит фоновой проверки: 64 МиБ. Используй отдельную проверку.","date",DateTimeOffset.Now.ToString("o")));continue;}
                        var result=engine.Scan(item.Key);string verdict=Store.Text(result,"verdict");
                        if(verdict=="not-scanned"){lock(gate)errors++;result["origin"]="realtime";Record(result);continue;}
                        lock(gate){if(fingerprints.Count>50000)fingerprints.Clear();fingerprints[item.Key]=stamp;}
                        if(verdict!="no-match")
                        {
                            result["origin"]="realtime";Record(result);
                            bool remove;lock(gate)remove=quarantine;
                            if(remove&&verdict=="known-threat")try{storage.Quarantine(result);}catch(Exception error){store.Journal("Собственный карантин","Ошибка",error.Message);}
                        }
                    }
                    catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException))throw;lock(gate)errors++;}
                }
            }
            catch(Exception error){try{store.Journal("Фоновый контроль","Ошибка",error.Message);}catch(IOException){}}
            finally{lock(gate)busy=false;}
        }
        internal void Record(Dictionary<string,object> result)
        {
            lock(Store.Gate)
            {
                var rows=Store.Array(Store.Get(store.Read("engine-findings.json"),"rows"));rows.Add(result);if(rows.Count>2000)rows=rows.Skip(rows.Count-2000).ToList();store.Write("engine-findings.json",Store.Map("rows",rows));
            }
            store.Journal("Собственный движок",Store.Text(result,"verdict")=="not-scanned"?"Ошибка":"Обнаружено",Store.Text(result,"path")+": "+Store.Text(result,"note"));
            if(Store.Text(result,"verdict")!="not-scanned")Notifications.Send(Store.Text(result,"path")+": "+Store.Text(result,"note"));
        }
        private void StopWatchers(){foreach(var watcher in watchers){watcher.EnableRaisingEvents=false;watcher.Dispose();}watchers.Clear();}
        public void Dispose(){lock(gate){disposed=true;enabled=false;StopWatchers();}timer.Dispose();}
    }
}
