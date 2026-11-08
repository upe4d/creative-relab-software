using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace PCCare
{
    public sealed class DiskSample
    {
        public string Name { get; set; }
        public string Free { get; set; }
        public string Total { get; set; }
        public long FreeBytes { get; set; }
        public long TotalBytes { get; set; }
        public double UsedPercent { get; set; }
    }
    public sealed class SystemSample
    {
        public double? CpuPercent;
        public bool CpuAvailable;
        public bool MemoryAvailable;
        public ulong TotalMemory, UsedMemory;
        public List<DiskSample> Disks = new List<DiskSample>();
        public List<string> Errors = new List<string>();
    }
    public sealed class SystemMonitor
    {
        [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
        {
            public uint Length, Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended;
        }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        private long previousIdle, previousTotal;
        public SystemSample Read()
        {
            var result = new SystemSample();
            long idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                result.CpuAvailable = true;
                long total = kernel + user, delta = total - previousTotal, idleDelta = idle - previousIdle;
                if (previousTotal != 0 && delta > 0) result.CpuPercent = Math.Max(0, Math.Min(100, 100.0 * (delta - idleDelta) / delta));
                previousTotal = total; previousIdle = idle;
            }
            else result.Errors.Add("CPU: ошибка Windows " + Marshal.GetLastWin32Error());
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (GlobalMemoryStatusEx(ref memory)) { result.MemoryAvailable = true; result.TotalMemory = memory.TotalPhysical; result.UsedMemory = memory.TotalPhysical - memory.AvailablePhysical; }
            else result.Errors.Add("Память: ошибка Windows " + Marshal.GetLastWin32Error());
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    long size = drive.TotalSize, free = drive.AvailableFreeSpace;
                    result.Disks.Add(new DiskSample { Name = drive.Name, Free = Format.Bytes(free), Total = Format.Bytes(size), FreeBytes = free, TotalBytes = size, UsedPercent = size > 0 ? 100.0 * (size - free) / size : 0 });
                }
                catch (IOException ex) { result.Errors.Add(drive.Name + ": " + ex.Message); }
                catch (UnauthorizedAccessException ex) { result.Errors.Add(drive.Name + ": " + ex.Message); }
            }
            return result;
        }
    }
}
