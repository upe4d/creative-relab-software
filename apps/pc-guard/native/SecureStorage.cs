using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PCGuard
{
    // Ключ резервов и карантина привязан к текущей учётной записи Windows через DPAPI.
    internal sealed class SecureStorage
    {
        private readonly Store store;private readonly object gate=new object();
        private readonly byte[] entropy=Encoding.UTF8.GetBytes("PCGuard-v2-local-storage");
        internal SecureStorage(Store store){this.store=store;Directory.CreateDirectory(Path.Combine(store.Data,"Quarantine"));Directory.CreateDirectory(Path.Combine(store.Data,"Recovery"));}
        private byte[] Key()
        {
            lock(gate)
            {
                string path=Path.Combine(store.Data,"storage-key.bin");
                if(!File.Exists(path))
                {
                    byte[] value=Vault.Random(64);
                    try{byte[] wrapped=ProtectedData.Protect(value,entropy,DataProtectionScope.CurrentUser);using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(wrapped,0,wrapped.Length);output.Flush(true);}}
                    finally{System.Array.Clear(value,0,value.Length);}
                }
                byte[] bytes;using(var input=SafeFiles.OpenStable(path,false)){using(var memory=new MemoryStream()){input.CopyTo(memory);bytes=memory.ToArray();}}
                byte[] key=ProtectedData.Unprotect(bytes,entropy,DataProtectionScope.CurrentUser);if(key.Length!=64)throw new CryptographicException("Неверный ключ локального хранилища.");return key;
            }
        }
        internal string Backup(string source,string area,string expectedHash=null)
        {
            source=SafeFiles.Full(source);if(area!="Recovery"&&area!="Quarantine")throw new ArgumentException("Неизвестное хранилище.");
            string id=Guid.NewGuid().ToString("N"),container=Path.Combine(store.Data,area,id+".pcgb");byte[] key=Key();
            try
            {
                using(var input=SafeFiles.OpenStable(source,false))
                {
                    string hash=SafeFiles.Hash(input);if(expectedHash!=null&&!hash.Equals(expectedHash,StringComparison.OrdinalIgnoreCase))throw new IOException("Файл изменился после проверки.");input.Position=0;
                    Vault.Seal(input,container,key,Vault.Random(32),Vault.Random(16),Vault.Iterations,Store.Map("name",Path.GetFileName(source),"size",input.Length,"sha256",hash));
                }
                using(var input=SafeFiles.OpenStable(container,false))Vault.Verify(input,key);
                return id;
            }
            finally{System.Array.Clear(key,0,key.Length);}
        }
        internal object Restore(string id,string area,string destination)
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(id??"",@"\A[a-f0-9]{32}\z")||(area!="Recovery"&&area!="Quarantine"))throw new ArgumentException("Некорректный резерв.");
            destination=SafeFiles.Full(destination);if(!Directory.Exists(destination))throw new IOException("Выбери существующую папку восстановления.");SafeFiles.Ordinary(destination);
            byte[] key=Key();try{using(var input=SafeFiles.OpenStable(Path.Combine(store.Data,area,id+".pcgb"),false))return Vault.Unseal(input,key,destination);}finally{System.Array.Clear(key,0,key.Length);}
        }
        internal object Quarantine(Dictionary<string,object> detection)
        {
            if(Store.Text(detection,"verdict")!="known-threat"&&Store.Text(detection,"verdict")!="test-detection")throw new InvalidOperationException("Для карантина нужна точная сигнатура или безопасная тестовая метка. Одной эвристики недостаточно.");
            string source=SafeFiles.Full(Store.Text(detection,"path"));if(SafeFiles.Within(source,store.Root)&&!SafeFiles.Within(source,Path.Combine(store.Root,"Incoming")))throw new IOException("Файлы PC Guard не помещаются в карантин.");
            if(SafeFiles.Within(source,Environment.GetFolderPath(Environment.SpecialFolder.Windows))||System.Text.RegularExpressions.Regex.IsMatch(source,@"(?i)\\IObit\\|\\Windows Defender\\|\\Microsoft\\Windows Defender\\"))throw new IOException("Файлы Windows, IObit и Defender не изменяются этим модулем.");
            string id=Guid.NewGuid().ToString("N"),container=Path.Combine(store.Data,"Quarantine",id+".pcgb");byte[] key=Key();
            var record=Store.Map("id",id,"path",source,"sha256",Store.Get(detection,"sha256"),"created",DateTimeOffset.Now.ToString("o"),"state","Prepared","verdict",Store.Get(detection,"verdict"));
            try
            {
                using(var input=SafeFiles.OpenStable(source,true))
                {
                    if(input.Length>536870912)throw new IOException("Файл больше лимита карантина.");string hash=SafeFiles.Hash(input);
                    if(!hash.Equals(Store.Text(detection,"sha256"),StringComparison.OrdinalIgnoreCase))throw new IOException("Файл изменился после обнаружения.");input.Position=0;
                    Vault.Seal(input,container,key,Vault.Random(32),Vault.Random(16),Vault.Iterations,Store.Map("name",Path.GetFileName(source),"size",input.Length,"sha256",hash));
                    using(var check=SafeFiles.OpenStable(container,false))Vault.Verify(check,key);
                    SaveQuarantine(record);SafeFiles.DeleteLocked(input);
                }
                record["state"]="Quarantined";SaveQuarantine(record);store.Journal("Собственный карантин","Выполнено",source);return record;
            }
            catch(Exception error)
            {
                record["state"]="NotRemoved";record["error"]=error.Message;if(File.Exists(container))SaveQuarantine(record);throw;
            }
            finally{System.Array.Clear(key,0,key.Length);}
        }
        private void SaveQuarantine(Dictionary<string,object> record)
        {
            lock(gate){var all=Store.Array(Store.Get(store.Read("quarantine.json"),"rows"));all.RemoveAll(v=>Store.Text(Store.Object(v),"id")==Store.Text(record,"id"));all.Add(record);store.Write("quarantine.json",Store.Map("rows",all));}
        }
        internal object QuarantineRows(){return Store.Array(Store.Get(store.Read("quarantine.json"),"rows"));}
        internal object RestoreQuarantine(string id,string destination)
        {
            var record=Store.Array(QuarantineRows()).Select(Store.Object).SingleOrDefault(r=>Store.Text(r,"id")==id);if(record==null)throw new IOException("Карантинная запись не найдена.");
            object result=Restore(id,"Quarantine",destination);record["state"]="Restored";SaveQuarantine(record);store.Journal("Возврат из собственного карантина","Выполнено",Store.Text(Store.Object(result),"path"));return result;
        }
    }
}
