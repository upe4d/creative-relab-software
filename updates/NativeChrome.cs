using System;using System.Runtime.InteropServices;
namespace CreativeRelab.Updates {
    internal static class NativeChrome {
        [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
        // Меняем только оформление своего окна, без реестра и системной темы.
        internal static void Apply(IntPtr handle){int value=1;try{if(DwmSetWindowAttribute(handle,20,ref value,4)!=0)DwmSetWindowAttribute(handle,19,ref value,4);}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}}
    }
}
