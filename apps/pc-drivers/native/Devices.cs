using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PCDrivers
{
    internal sealed class Device
    {
        public string Id, Name, Class, Manufacturer, Service, Inf, Provider, Version, Date, MatchingId;
        public string[] HardwareIds, CompatibleIds;
        public uint? Problem;
        public string ReadError;
        public string Status { get { return !Problem.HasValue ? "Не проверено" : Problem.Value == 0 ? "Работает" : "Код " + Problem; } }
    }
    internal sealed class DriverCandidate
    {
        public string Description, Manufacturer, Provider, Version, Date, Inf;
        public uint Rank;
    }
    // Оборудование читается через SetupAPI, без WMI и запуска стороннего сканера.
    internal static class Devices
    {
        [StructLayout(LayoutKind.Sequential)] private struct DevInfo { public uint Size; public Guid ClassGuid; public uint Inst; public UIntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct InstallParams
        {
            public uint Size, Flags, FlagsEx; public IntPtr Parent, Handler, Context, Queue; public UIntPtr Reserved; public uint Reserved2;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DriverInfo
        {
            public uint Size, Type; public UIntPtr Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Manufacturer;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Provider;
            public System.Runtime.InteropServices.ComTypes.FILETIME Date;
            public ulong Version;
        }
        [StructLayout(LayoutKind.Sequential)] private struct DriverParams { public uint Size, Rank, Flags; public UIntPtr Private; public uint Reserved; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(IntPtr cls, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfo info);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DevInfo info, StringBuilder id, uint size, out uint required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DevInfo info, uint property, out uint kind, byte[] data, uint size, out uint needed);
        [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint inst, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr cls, IntPtr parent);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string id, IntPtr parent, uint flags, ref DevInfo info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, ref DevInfo info, ref InstallParams parameters);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiSetDeviceInstallParamsW(IntPtr set, ref DevInfo info, ref InstallParams parameters);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref DevInfo info, uint type);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiEnumDriverInfoW(IntPtr set, ref DevInfo info, uint type, uint index, ref DriverInfo driver);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDriverInstallParamsW(IntPtr set, ref DevInfo info, ref DriverInfo driver, ref DriverParams parameters);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDriverInfoList(IntPtr set, ref DevInfo info, uint type);
        private static DevInfo Info() { return new DevInfo { Size = (uint)Marshal.SizeOf(typeof(DevInfo)) }; }
        private static void Require(bool ok, string action) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), action); }
        private static string[] Property(IntPtr set, ref DevInfo info, uint property)
        {
            uint kind, size; byte[] data = new byte[65536];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out kind, data, (uint)data.Length, out size))
            {
                int e = Marshal.GetLastWin32Error(); if (e == 13 || e == 1168) return new string[0]; throw new Win32Exception(e, "Чтение свойства устройства " + property);
            }
            if (size > data.Length || (kind != 1 && kind != 7)) throw new InvalidDataException("Некорректное свойство устройства");
            return Encoding.Unicode.GetString(data, 0, (int)size).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }
        private static string One(IntPtr set, ref DevInfo info, uint property) { return Property(set, ref info, property).FirstOrDefault() ?? ""; }
        public static List<Device> Scan()
        {
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 6);
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Список устройств");
            var rows = new List<Device>();
            try
            {
                for (uint index = 0; index < 20000; index++)
                {
                    DevInfo info = Info();
                    if (!SetupDiEnumDeviceInfo(set, index, ref info)) { int e = Marshal.GetLastWin32Error(); if (e == 259) return rows; throw new Win32Exception(e); }
                    var id = new StringBuilder(4096); uint needed;
                    Require(SetupDiGetDeviceInstanceIdW(set, ref info, id, (uint)id.Capacity, out needed), "Идентификатор устройства");
                    var d = new Device { Id = id.ToString(), HardwareIds = new string[0], CompatibleIds = new string[0], ReadError = "", Inf = "", Provider = "", Version = "", Date = "", MatchingId = "" };
                    try
                    {
                        d.Name = One(set, ref info, 12); if (d.Name.Length == 0) d.Name = One(set, ref info, 0);
                        d.Class = One(set, ref info, 7); d.Manufacturer = One(set, ref info, 11); d.Service = One(set, ref info, 4);
                        d.HardwareIds = Property(set, ref info, 1); d.CompatibleIds = Property(set, ref info, 2);
                        uint status, problem; uint result = CM_Get_DevNode_Status(out status, out problem, info.Inst, 0);
                        if (result == 0) d.Problem = problem; else d.ReadError = "Статус ConfigMgr: " + result;
                        string driver = One(set, ref info, 9);
                        if (driver.Length > 0)
                        using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        using (RegistryKey key = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver))
                        {
                            if (key != null) { d.Inf = Convert.ToString(key.GetValue("InfPath")); d.Provider = Convert.ToString(key.GetValue("ProviderName")); d.Version = Convert.ToString(key.GetValue("DriverVersion")); d.Date = Convert.ToString(key.GetValue("DriverDate")); d.MatchingId = Convert.ToString(key.GetValue("MatchingDeviceId")); }
                        }
                    }
                    catch (Exception ex) { d.ReadError = ex.Message; }
                    if (String.IsNullOrEmpty(d.Name)) d.Name = d.Id;
                    DateTime driverDate; if (DateTime.TryParseExact(d.Date, new[] { "M-d-yyyy", "M/d/yyyy", "yyyy-MM-dd" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out driverDate)) d.Date = driverDate.ToString("dd.MM.yyyy");
                    rows.Add(d);
                }
                throw new InvalidDataException("Превышен лимит устройств");
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        // Windows сама разбирает секции INF, ограничения ОС, архитектуру и ранг совпадения.
        // Список создаётся только в памяти: функции установки драйвера не вызываются.
        public static List<DriverCandidate> Match(string id, string inf)
        {
            inf = Path.GetFullPath(inf);
            if (inf.Length >= 260 || !inf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) || !File.Exists(inf)) throw new ArgumentException("Выберите существующий INF с путём короче 260 символов");
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            DevInfo info = Info(); bool built = false;
            try
            {
                Require(SetupDiOpenDeviceInfoW(set, id, IntPtr.Zero, 0, ref info), "Открытие выбранного устройства");
                var p = new InstallParams { Size = (uint)Marshal.SizeOf(typeof(InstallParams)) };
                Require(SetupDiGetDeviceInstallParamsW(set, ref info, ref p), "Чтение параметров подбора");
                p.Flags |= 0x10000; p.DriverPath = inf;
                Require(SetupDiSetDeviceInstallParamsW(set, ref info, ref p), "Выбор единственного INF");
                Require(SetupDiBuildDriverInfoList(set, ref info, 2), "Подбор совместимых моделей"); built = true;
                var result = new List<DriverCandidate>();
                for (uint i = 0; i < 4096; i++)
                {
                    var d = new DriverInfo { Size = (uint)Marshal.SizeOf(typeof(DriverInfo)) };
                    if (!SetupDiEnumDriverInfoW(set, ref info, 2, i, ref d)) { int e = Marshal.GetLastWin32Error(); if (e == 259) return result.OrderBy(r => r.Rank).ToList(); throw new Win32Exception(e); }
                    var rank = new DriverParams { Size = (uint)Marshal.SizeOf(typeof(DriverParams)) };
                    Require(SetupDiGetDriverInstallParamsW(set, ref info, ref d, ref rank), "Ранг драйвера");
                    long time = ((long)(uint)d.Date.dwHighDateTime << 32) | (uint)d.Date.dwLowDateTime;
                    result.Add(new DriverCandidate { Inf = inf, Description = d.Description, Manufacturer = d.Manufacturer, Provider = d.Provider, Rank = rank.Rank, Version = VersionText(d.Version), Date = time > 0 ? DateTime.FromFileTimeUtc(time).ToString("dd.MM.yyyy") : "Не указана" });
                }
                throw new InvalidDataException("Превышен лимит моделей INF");
            }
            finally { if (built) SetupDiDestroyDriverInfoList(set, ref info, 2); SetupDiDestroyDeviceInfoList(set); }
        }
        internal static string VersionText(ulong v) { return ((v >> 48) & 65535) + "." + ((v >> 32) & 65535) + "." + ((v >> 16) & 65535) + "." + (v & 65535); }
        internal static object Sizes() { return Store.Map("device", Marshal.SizeOf(typeof(DevInfo)), "install", Marshal.SizeOf(typeof(InstallParams)), "driver", Marshal.SizeOf(typeof(DriverInfo)), "rank", Marshal.SizeOf(typeof(DriverParams))); }
    }
}
