using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
namespace PCGuard
{
    // Брокер не повышает редактор: для полного UAC-токена берётся связанный ограниченный токен.
    internal static class EditorLauncher
    {
        const uint TokenQuery=8,TokenDuplicate=2,TokenAssignPrimary=1,LogonWithProfile=1,UnicodeEnvironment=0x400;
        const int ElevationTypeClass=18,LinkedTokenClass=19,ElevationFull=2;
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct Startup{internal int Size;internal string Reserved,Desktop,Title;internal uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags;internal short Show,Reserved2;internal IntPtr ReservedPtr,Input,Output,Error;}
        [StructLayout(LayoutKind.Sequential)]struct ProcessInfo{internal IntPtr Process,Thread;internal uint Pid,Tid;}
        [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(IntPtr p,uint access,out IntPtr token);
        [DllImport("advapi32.dll",EntryPoint="GetTokenInformation",SetLastError=true)]static extern bool GetElevation(IntPtr token,int kind,out int data,int bytes,out int returned);
        [DllImport("advapi32.dll",EntryPoint="GetTokenInformation",SetLastError=true)]static extern bool GetLinked(IntPtr token,int kind,out IntPtr data,int bytes,out int returned);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcessWithTokenW(IntPtr token,uint logon,string application,StringBuilder command,uint flags,IntPtr environment,string directory,ref Startup startup,out ProcessInfo process);
        internal static object Start(string path)
        {

            if(path.IndexOf('"')>=0)throw new IOException("Некорректный путь редактора.");
            IntPtr primary=IntPtr.Zero,linked=IntPtr.Zero;
            try{
                if(!OpenProcessToken(GetCurrentProcess(),TokenQuery|TokenDuplicate|TokenAssignPrimary,out primary))throw new Win32Exception();
                using(var identity=new WindowsIdentity(primary)){if(identity.IsSystem)throw new IOException("Редактор запускается только из пользовательского интерфейса, не от SYSTEM.");}
                int elevation,returned;if(!GetElevation(primary,ElevationTypeClass,out elevation,4,out returned))throw new Win32Exception();
                if(elevation==ElevationFull&&!GetLinked(primary,LinkedTokenClass,out linked,IntPtr.Size,out returned))throw new Win32Exception();
                var startup=new Startup{Size=Marshal.SizeOf(typeof(Startup)),Desktop=@"winsta0\default"};ProcessInfo child;
                if(!CreateProcessWithTokenW(linked!=IntPtr.Zero?linked:primary,LogonWithProfile,path,new StringBuilder("\""+path+"\""),UnicodeEnvironment,IntPtr.Zero,Path.GetDirectoryName(path),ref startup,out child))throw new Win32Exception();
                try{return Store.Map("pid",child.Pid,"path",path,"usedFilteredToken",linked!=IntPtr.Zero);}finally{CloseHandle(child.Thread);CloseHandle(child.Process);}
            }finally{if(linked!=IntPtr.Zero)CloseHandle(linked);if(primary!=IntPtr.Zero)CloseHandle(primary);}
        }
    }
}