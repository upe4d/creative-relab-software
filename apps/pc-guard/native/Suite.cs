using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCGuard
{
    // Собственные модули не вызывают Defender/IObit и не меняют их конфигурацию.
    internal sealed class Suite:IDisposable
    {
        private readonly Store store;internal readonly Engine Engine;internal readonly Vault Vault;internal readonly SecureStorage Storage;
        internal readonly Realtime Realtime;internal readonly FolderProtection Folders;
        internal readonly Autostart Autostart;
        internal readonly DriverBridge Driver;
        private readonly object jobsGate=new object();private readonly Dictionary<string,CancellationTokenSource> jobs=new Dictionary<string,CancellationTokenSource>();
        internal Suite(Store store,bool resume=true)
        {
            this.store=store;Engine=new Engine(store);Vault=new Vault(store);Storage=new SecureStorage(store);Realtime=new Realtime(store,Engine,Storage);Folders=new FolderProtection(store,Storage);Autostart=new Autostart(store);Driver=new DriverBridge(store);
            if(resume)
            {
                try{var saved=store.Read("realtime.json");if(saved.Count==0){string incoming=Path.Combine(store.Root,"Incoming");Directory.CreateDirectory(incoming);Realtime.Configure(new[]{incoming},true,true);}else if(Object.Equals(Store.Get(saved,"enabled"),true))Realtime.Configure(Store.Array(Store.Get(saved,"roots")).Select(Convert.ToString),true,Object.Equals(Store.Get(saved,"quarantine"),true));}
                catch(Exception error){store.Journal("Запуск фонового контроля","Ошибка",error.Message);}
                try{Folders.Resume();}catch(Exception error){store.Journal("Возврат защиты папки","Ошибка",error.Message);}
            }
        }
        internal object Status(){return Store.Map("engine",Engine.Info(),"rulesUpdate",store.Read("rule-update-state.json"),"realtime",Realtime.Status(),"folders",Folders.Status(),"vault",Vault.List(),"autostart",Autostart.Status(),"driver",Driver.Status());}
        internal bool Busy{get{lock(jobsGate)return jobs.Count>0;}}
        private string Start(string action,Func<CancellationToken,object> work)
        {
            lock(jobsGate)
            {
                if(jobs.Count>=2)throw new InvalidOperationException("Дождись завершения текущих собственных операций.");
                string id=Guid.NewGuid().ToString("N");var cancel=new CancellationTokenSource();jobs.Add(id,cancel);
                SaveJob(id,Store.Map("id",id,"action",action,"state","running","message","Операция выполняется"));
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try{object data=work(cancel.Token);SaveJob(id,Store.Map("id",id,"action",action,"state","done","message","Операция завершена","data",data));store.Journal("PC Guard: "+action,"Выполнено","Собственная операция завершена.","own-"+id);}
                    catch(Exception error){SaveJob(id,Store.Map("id",id,"action",action,"state","error","message",error.Message));store.Journal("PC Guard: "+action,"Ошибка",error.Message,"own-"+id);}
                    finally{lock(jobsGate)jobs.Remove(id);cancel.Dispose();}
                });return id;
            }
        }
        private void SaveJob(string id,object result){lock(Store.Gate)Store.WritePath(Path.Combine(store.Data,"own-job-"+id+".json"),result);}
        private object GetJob(string id)
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(id??"",@"\A[a-f0-9]{32}\z"))throw new ArgumentException("Некорректный идентификатор операции.");
            string path=Path.Combine(store.Data,"own-job-"+id+".json");if(!File.Exists(path))throw new IOException("Операция не найдена.");return Store.ReadPath(path,null);
        }
        internal object Dispatch(string action,Dictionary<string,object> input)
        {
            switch(action)
            {
                case "suite_status":return Status();
                case "own_database_update":Confirm(input);return Store.Map("ownJob",Start("Обновление собственной базы PC Guard",delegate(CancellationToken cancel){return new RuleUpdater(store,Engine).Update(cancel);}));
                case "own_driver_configure":Confirm(input);return Driver.Configure(input,Engine);
                case "own_driver_launch_editor":Confirm(input);return Driver.LaunchEditor(input);
                case "suite_job":return GetJob(Store.Text(input,"id"));
                case "own_scan":
                    string scanPath=SafeFiles.Full(Store.Text(input,"path"));
                    return Store.Map("ownJob",Start("Проверка собственным движком",delegate(CancellationToken cancel){var result=Store.Object(Engine.ScanTree(scanPath,cancel));store.Write("last-own-scan.json",result);foreach(var row in Store.Array(Store.Get(result,"rows")).Select(Store.Object))if(Store.Text(row,"verdict")!="no-match")Realtime.Record(row);return result;}));
                case "own_cancel":
                    string cancelId=Store.Text(input,"id");lock(jobsGate){CancellationTokenSource cancellation;if(jobs.TryGetValue(cancelId,out cancellation))cancellation.Cancel();}return Store.Map("requested",true);
                case "own_findings":return Store.Map("rows",Store.Get(store.Read("engine-findings.json"),"rows",new object[0]),"lastScan",store.Read("last-own-scan.json"),"quarantine",Storage.QuarantineRows());
                case "own_database_import":
                    Confirm(input);return Engine.Import(SafeFiles.Full(Store.Text(input,"path")),Store.Text(input,"sha256"));
                case "own_autostart":Confirm(input);return Autostart.Set(Object.Equals(Store.Get(input,"enabled"),true));
                case "own_realtime":
                    Confirm(input);var roots=Store.Array(Store.Get(input,"roots")).Select(Convert.ToString);return Realtime.Configure(roots,Object.Equals(Store.Get(input,"enabled"),true),Object.Equals(Store.Get(input,"quarantine"),true));
                case "own_quarantine":
                    Confirm(input);string findingId=Store.Text(input,"id");var finding=Store.Array(Store.Get(store.Read("engine-findings.json"),"rows")).Select(Store.Object).LastOrDefault(r=>Store.Text(r,"id")==findingId);
                    if(finding==null)throw new IOException("Находка не найдена.");return Storage.Quarantine(finding);
                case "own_restore":
                    Confirm(input);return Storage.RestoreQuarantine(Store.Text(input,"id"),SafeFiles.Full(Store.Text(input,"destination")));
                case "vault_list":return Vault.List();
                case "vault_encrypt":
                    Confirm(input);string source=SafeFiles.Full(Store.Text(input,"path")),encryptPassword=Store.Text(input,"password");
                    return Store.Map("ownJob",Start("Добавление в сейф",delegate(CancellationToken cancel){try{return Vault.Encrypt(source,encryptPassword);}finally{encryptPassword=null;}}));
                case "vault_decrypt":
                    Confirm(input);string vaultId=Store.Text(input,"id"),destination=SafeFiles.Full(Store.Text(input,"destination")),decryptPassword=Store.Text(input,"password");
                    return Store.Map("ownJob",Start("Восстановление из сейфа",delegate(CancellationToken cancel){try{return Vault.Decrypt(vaultId,destination,decryptPassword);}finally{decryptPassword=null;}}));
                case "folder_protect":
                    Confirm(input);string folder=SafeFiles.Full(Store.Text(input,"path"));bool lockWrites=Object.Equals(Store.Get(input,"lockWrites"),true);
                    return Store.Map("ownJob",Start("Защита папки",delegate(CancellationToken cancel){return Folders.Protect(folder,lockWrites);}));
                case "folder_unlock":Confirm(input);return Folders.Unlock();
                case "folder_restore":
                    Confirm(input);return Folders.Restore(Store.Text(input,"id"),SafeFiles.Full(Store.Text(input,"destination")));
                case "folder_alerts":return store.Read("folder-alerts.json");
                default:throw new ArgumentException("Неизвестная собственная операция.");
            }
        }
        private static void Confirm(Dictionary<string,object> input){if(!Object.Equals(Store.Get(input,"confirmed"),true))throw new ArgumentException("Действие требует подтверждения.");}
        public void Dispose(){Realtime.Dispose();Folders.Dispose();lock(jobsGate)foreach(var job in jobs.Values)job.Cancel();}
    }
}
