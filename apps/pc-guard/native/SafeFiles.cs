using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PCGuard
{
    // Проверка границ и работа с одним файловым дескриптором предотвращают подмену при карантине.
    internal static class SafeFiles
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref Disposition info, uint length);
        [StructLayout(LayoutKind.Sequential)] private struct Disposition { [MarshalAs(UnmanagedType.U1)] internal bool Delete; }
        internal static string Full(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length > 240 || !System.Text.RegularExpressions.Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]")) throw new IOException("Нужен локальный путь длиной до 240 символов.");
            string full = Path.GetFullPath(path); if (full.IndexOf(':', 2) >= 0) throw new IOException("Альтернативные потоки не поддерживаются."); return full;
        }
        internal static bool Within(string path, string root)
        {
            path = Full(path); root = Full(root).TrimEnd('\\'); return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }
        internal static void Ordinary(string path)
        {
            string node = Full(path);
            while (!String.IsNullOrEmpty(node))
            {
                if ((File.GetAttributes(node) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки и junction пропущены: " + node);
                node = Path.GetDirectoryName(node);
            }
        }
        internal static FileStream OpenStable(string path, bool allowDelete)
        {
            path = Full(path); Ordinary(path);
            var handle = CreateFile(path, 0x80000000U | (allowDelete ? 0x10000U : 0), 1, IntPtr.Zero, 3, 0x00200000U | 0x08000000U, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Файл занят или недоступен: " + new Win32Exception(error).Message, new Win32Exception(error)); }
            try { return new FileStream(handle, FileAccess.Read, 65536, false); } catch { handle.Dispose(); throw; }
        }
        internal static void DeleteLocked(FileStream stream)
        {
            var info = new Disposition { Delete = true };
            if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, ref info, (uint)Marshal.SizeOf(typeof(Disposition)))) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось удалить оригинал по проверенному дескриптору.");
        }
        internal static string Hash(Stream stream)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        internal static string HashFile(string path) { using (var file = OpenStable(path, false)) return Hash(file); }
        internal static void CreateParent(string path)
        {
            string parent = Path.GetDirectoryName(Full(path)); if (!Directory.Exists(parent)) throw new IOException("Папка назначения отсутствует."); Ordinary(parent);
        }
    }
}
