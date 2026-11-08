using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
namespace PCGuard
{
    // API показывает реальный ответ устройства. Наличие SYS на диске не означает включённую защиту.
    internal sealed class DriverBridge
    {
        private readonly Store store;internal DriverBridge(Store store){this.store=store;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inputSize,byte[] output,int outputSize,out int returned,IntPtr overlapped);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint QueryDosDevice(string drive,StringBuilder target,int length);
        private const uint Read=0x80000000,Write=0x40000000,StatusCode=0x226400,ConfigureCode=0x22A404,CommitCode=0x22A408,AbortCode=0x22A40C;
        private SafeFileHandle Open(bool write){var handle=CreateFile(@"\\.\PCGuardControl",Read|(write?Write:0),3,IntPtr.Zero,3,0,IntPtr.Zero);if(handle.IsInvalid){int code=Marshal.GetLastWin32Error();handle.Dispose();throw new IOException("Драйвер недоступен: "+new Win32Exception(code).Message,new Win32Exception(code));}return handle;}
        internal object Status()
        {
            bool registered;using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PCGuardFilter"))registered=key!=null;
            var release=Store.Object(Store.ReadPath(Path.Combine(store.Root,"driver","release.json"),new Dictionary<string,object>()));
            string candidate=Path.Combine(store.Root,"driver","build","PCGuardFilter.sys");bool tested=false;
            if(Object.Equals(Store.Get(release,"kernelRuntimeTested"),true)&&File.Exists(candidate))try{tested=SafeFiles.HashFile(candidate).Equals(Store.Text(release,"kernelTestSha256"),StringComparison.OrdinalIgnoreCase);}catch(IOException){}
            var result=Store.Map("compiled",File.Exists(Path.Combine(store.Root,"driver","build","PCGuardFilter.sys")),"serviceRegistered",registered,"installed",registered,"reachable",false,"preExecutionBlocking",null,"folderWriteFiltering",null,"perAppCameraBlocking",false,"kernelRuntimeTested",tested,"kernelChecks",tested?Store.Get(release,"kernelChecks",0):0,"kernelFailures",tested?Store.Get(release,"kernelFailures",0):0,"verifierRuntimeTested",tested&&Object.Equals(Store.Get(release,"verifierRuntimeTested"),true),"saved",store.Read("driver-config.json"));
            try{using(var handle=Open(false)){byte[] output=new byte[52];int count;if(!DeviceIoControl(handle,StatusCode,null,0,output,output.Length,out count,IntPtr.Zero)||count!=52||BitConverter.ToUInt32(output,0)!=4)throw new IOException("Не совпал протокол драйвера.");uint flags=BitConverter.ToUInt32(output,4);result["preparing"]=BitConverter.ToUInt32(output,44)!=0;result["protectedIds"]=BitConverter.ToUInt32(output,48);result["reachable"]=true;result["preExecutionBlocking"]=(flags&1)!=0;result["folderWriteFiltering"]=(flags&2)!=0;result["flags"]=flags;result["hashes"]=BitConverter.ToUInt32(output,8);result["execRoots"]=BitConverter.ToUInt32(output,12);result["protectedRoots"]=BitConverter.ToUInt32(output,16);result["allowedApps"]=BitConverter.ToUInt32(output,20);result["blockedStarts"]=BitConverter.ToUInt32(output,28);result["blockedWrites"]=BitConverter.ToUInt32(output,32);result["scanErrors"]=BitConverter.ToUInt32(output,36);}}
            catch(IOException error){result["reason"]=error.Message;var native=error.InnerException as Win32Exception;if(native!=null&&(native.NativeErrorCode==2||native.NativeErrorCode==3)){result["preExecutionBlocking"]=false;result["folderWriteFiltering"]=false;}}
            return result;
        }
        private static string NtPath(string path,bool directory)
        {
            path=SafeFiles.Full(path).TrimEnd('\\');SafeFiles.Ordinary(path);
            if(directory&&!Directory.Exists(path))throw new IOException("Нет папки: "+path);
            if(!directory&&!File.Exists(path))throw new IOException("Нет приложения: "+path);
            if(directory&&path.Length<4)throw new IOException("Весь диск выбирать нельзя.");
            var drive=new DriveInfo(Path.GetPathRoot(path));if(drive.DriveFormat!="NTFS"&&drive.DriveFormat!="ReFS")throw new IOException("Драйвер рассчитан только на NTFS/ReFS.");
            var buffer=new StringBuilder(1024);if(QueryDosDevice(path.Substring(0,2),buffer,buffer.Capacity)==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            string result=buffer.ToString()+path.Substring(2);if(!result.StartsWith(@"\Device\HarddiskVolume",StringComparison.OrdinalIgnoreCase)||result.Length>=512)throw new IOException("Неподдерживаемый путь тома.");return result;
        }
        private static void PutPath(BinaryWriter writer,string path){if(path.Length>=512)throw new IOException("Слишком длинный NT-путь.");writer.Write(Encoding.Unicode.GetBytes(path));writer.Write(new byte[(512-path.Length)*2]);}
        internal static byte[] HashBytes(string hash){if(!System.Text.RegularExpressions.Regex.IsMatch(hash??"",@"\A[0-9a-fA-F]{64}\z"))throw new IOException("Некорректный хеш политики.");return Enumerable.Range(0,32).Select(i=>Convert.ToByte(hash.Substring(i*2,2),16)).ToArray();}
        internal object LaunchEditor(Dictionary<string,object> input)
        {
            var config=store.Read("driver-config.json");var apps=Store.Array(Store.Get(config,"allowedApps"));int index;
            if(!Int32.TryParse(Store.Text(input,"index"),out index)||index<0||index>=apps.Count)throw new IOException("Выберите редактор из сохранённой политики.");
            string selected=Convert.ToString(apps[index]),path=SafeFiles.Full(selected);
            string expected=Store.Text(Store.Object(Store.Get(config,"allowedAppHashes")),selected);
            if(!System.Text.RegularExpressions.Regex.IsMatch(expected,@"\A[0-9a-fA-F]{64}\z"))throw new IOException("Примените политику заново: сохранённого SHA редактора нет.");
            using(var stable=SafeFiles.OpenStable(path,false)){
                if(!SafeFiles.Hash(stable).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("Файл редактора изменился. Сначала проверьте его и заново согласуйте политику.");
                using(var handle=Open(true)){int count;if(!DeviceIoControl(handle,0x22A410,null,0,null,0,out count,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"Драйвер не подтвердил защищённый процесс запуска.");}
                object result=EditorLauncher.Start(path);store.Journal("Защищённый запуск редактора","Выполнено",path);return result;
            }
        }
        internal object Configure(Dictionary<string,object> input,Engine engine)
        {
            // Сначала проверяем доступ, поэтому без установленного драйвера настройки не сохраняются как активные.
            using(var handle=Open(true))
            {
                var exec=Store.Array(Store.Get(input,"execRoots")).Select(Convert.ToString).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var folders=Store.Array(Store.Get(input,"protectedRoots")).Select(Convert.ToString).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var apps=Store.Array(Store.Get(input,"allowedApps")).Select(Convert.ToString).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if(exec.Length>16||folders.Length>16||apps.Length>64)throw new IOException("Предел: 16 папок каждого типа и 64 приложения.");
                foreach(string path in exec.Concat(folders)){string full=SafeFiles.Full(path);if(SafeFiles.Within(full,Environment.GetFolderPath(Environment.SpecialFolder.Windows))||SafeFiles.Within(full,store.Root)||System.Text.RegularExpressions.Regex.IsMatch(full,@"(?i)\\IObit(?:\\|$)|\\Windows Defender(?:\\|$)"))throw new IOException("Системные каталоги, IObit, Defender и PC Guard не выбираются.");}
                var appHashes=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
                var hashes=engine.BlockHashes();if(hashes.Length>32768)throw new IOException("Драйвер принимает до 32768 блокирующих хешей.");
                var execNt=exec.Select(p=>NtPath(p,true)).ToArray();var folderNt=folders.Select(p=>NtPath(p,true)).ToArray();
                using(var bytes=new MemoryStream())using(var writer=new BinaryWriter(bytes))
                {
                    writer.Write(4U);writer.Write((uint)((exec.Length>0?1:0)|(folders.Length>0?2:0)|4));writer.Write((uint)hashes.Length);writer.Write((uint)exec.Length);writer.Write((uint)folders.Length);writer.Write((uint)apps.Length);writer.Write(0U);
                    foreach(string path in execNt.Concat(folderNt))PutPath(writer,path);
                    foreach(string path in apps){if(!Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase))throw new IOException("Разрешённое приложение должно быть EXE.");PutPath(writer,NtPath(path,false));string appHash=SafeFiles.HashFile(path);appHashes[path]=appHash;writer.Write(HashBytes(appHash));}
                    foreach(string hash in hashes)writer.Write(HashBytes(hash));writer.Flush();byte[] policy=bytes.ToArray();int count;
                    if(!DeviceIoControl(handle,ConfigureCode,policy,policy.Length,null,0,out count,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"Драйвер отклонил подготовку политики.");
                    bool committed=false;
                    try{using(var catalog=new ProtectedCatalog(folders)){
                        ulong[] probes=catalog.Handles();byte[] commit=new byte[policy.Length+probes.Length*8];Buffer.BlockCopy(policy,0,commit,0,policy.Length);Buffer.BlockCopy(BitConverter.GetBytes((uint)probes.Length),0,commit,24,4);
                        for(int i=0;i<probes.Length;i++)Buffer.BlockCopy(BitConverter.GetBytes(probes[i]),0,commit,policy.Length+i*8,8);
                        if(!DeviceIoControl(handle,CommitCode,commit,commit.Length,null,0,out count,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"Политика не включена: занятые файлы, старые пишущие отображения или ошибка проверки. Старая политика сохранена.");committed=true;
                    }}finally{if(!committed)DeviceIoControl(handle,AbortCode,null,0,null,0,out count,IntPtr.Zero);}
                }
                store.Write("driver-config.json",Store.Map("execRoots",exec,"protectedRoots",folders,"allowedApps",apps,"allowedAppHashes",appHashes));
                store.Journal("Системный фильтр PC Guard","Выполнено","Политика подтверждена драйвером после проверки существующих файлов и потоков. Для защищённой записи редакторы запускаются кнопкой PC Guard; сценарии и память не покрываются.");
            }
            return Status();
        }
    }
}
