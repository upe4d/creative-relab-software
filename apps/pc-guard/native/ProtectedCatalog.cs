using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace PCGuard
{
    // Во время подготовки драйвер замораживает новые изменения. Здесь удерживаются все существующие потоки.
    internal sealed class ProtectedCatalog : IDisposable
    {
        private readonly List<FileStream> files=new List<FileStream>();
        private readonly List<SafeFileHandle> directories=new List<SafeFileHandle>();
        private readonly HashSet<string> visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly DateTime deadline=DateTime.UtcNow.AddSeconds(30);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct StreamData{internal long Size;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=296)]internal string Name;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr FindFirstStreamW(string path,int level,out StreamData data,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool FindNextStreamW(IntPtr find,out StreamData data);
        [DllImport("kernel32.dll")]private static extern bool FindClose(IntPtr handle);
        internal ProtectedCatalog(IEnumerable<string> roots){try{foreach(string root in roots)Walk(SafeFiles.Full(root));}catch{Dispose();throw;}}
        private void Bound(){if(DateTime.UtcNow>deadline)throw new IOException("Подготовка защиты превысила 30 секунд; старая политика сохранена.");}
        private void Hold(string path,bool alternate)
        {
            Bound();if(files.Count>=2000)throw new IOException("Для драйвера допускается до 2000 файловых потоков. Выберите меньшие папки.");
            if(!alternate){files.Add(SafeFiles.OpenStable(path,false));return;}
            var handle=CreateFile(path,0x80000000,1,IntPtr.Zero,3,0x08200000,IntPtr.Zero);
            if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new IOException("Не удалось удержать дополнительный поток: "+new Win32Exception(error).Message);}
            try{files.Add(new FileStream(handle,FileAccess.Read,4096,false));}catch{handle.Dispose();throw;}
        }
        private void Streams(string path)
        {
            StreamData data;IntPtr find=FindFirstStreamW(path,0,out data,0);
            if(find==new IntPtr(-1)){int error=Marshal.GetLastWin32Error();if(error==38)return;throw new IOException("Не удалось перечислить потоки: "+path,new Win32Exception(error));}
            try{do{Bound();if(data.Name=="::$DATA")continue;if(!System.Text.RegularExpressions.Regex.IsMatch(data.Name??"",@"\A:[^:\\/\x00-\x1f]+:\$DATA\z")||path.Length+data.Name.Length>500)throw new IOException("Неподдерживаемое имя дополнительного потока.");Hold(path+data.Name,true);}while(FindNextStreamW(find,out data));int error=Marshal.GetLastWin32Error();if(error!=38)throw new Win32Exception(error,"Не закончено перечисление потоков.");}finally{FindClose(find);}
        }
        private void Walk(string path)
        {
            Bound();if(!visited.Add(path.TrimEnd('\\')))return;if(visited.Count>2000)throw new IOException("Слишком много каталогов для подготовки политики.");SafeFiles.Ordinary(path);
            var directory=CreateFile(path,0x80,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
            if(directory.IsInvalid){int error=Marshal.GetLastWin32Error();directory.Dispose();throw new IOException("Каталог занят или недоступен: "+path,new Win32Exception(error));}
            directories.Add(directory);Streams(path);
            foreach(string entry in Directory.EnumerateFileSystemEntries(path)){Bound();string full=SafeFiles.Full(entry);FileAttributes attributes=File.GetAttributes(full);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("В защищаемой папке найдена ссылка: "+full);if((attributes&FileAttributes.Directory)!=0)Walk(full);else{Hold(full,false);Streams(full);}}
        }
        internal ulong[] Handles(){var result=new ulong[files.Count];for(int i=0;i<files.Count;i++)result[i]=unchecked((ulong)files[i].SafeFileHandle.DangerousGetHandle().ToInt64());return result;}
        public void Dispose(){foreach(var file in files)file.Dispose();files.Clear();for(int i=directories.Count-1;i>=0;i--)directories[i].Dispose();directories.Clear();}
    }
}
