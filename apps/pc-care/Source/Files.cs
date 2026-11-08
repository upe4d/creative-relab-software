using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PCCare
{
    public sealed class WalkStats
    {
        public int Entries, Files, Skipped;
        public bool Limited;
        public Stopwatch Clock = Stopwatch.StartNew();
    }

    public static class FileWalker
    {
        public static void Walk(string root, Settings settings, CancellationToken cancel, WalkStats stats,
            Action<FileInfo> found, Func<string, bool> skipDirectory)
        {
            if (!Directory.Exists(root)) return;
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0 && !stats.Limited)
            {
                cancel.ThrowIfCancellationRequested();
                string directory = stack.Pop();
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { stats.Skipped++; continue; }
                    foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (++stats.Entries > settings.ScanEntryLimit || stats.Files >= settings.ScanFileLimit || stats.Clock.Elapsed.TotalSeconds >= settings.ScanTimeLimitSeconds)
                        { stats.Limited = true; break; }
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) { stats.Skipped++; continue; }
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (skipDirectory == null || !skipDirectory(path)) stack.Push(path);
                            }
                            else { stats.Files++; found(new FileInfo(path)); }
                        }
                        catch (UnauthorizedAccessException) { stats.Skipped++; }
                        catch (IOException) { stats.Skipped++; }
                        catch (System.Security.SecurityException) { stats.Skipped++; }
                    }
                }
                catch (UnauthorizedAccessException) { stats.Skipped++; }
                catch (IOException) { stats.Skipped++; }
                catch (System.Security.SecurityException) { stats.Skipped++; }
            }
        }
    }

    public sealed class CleanupRoot
    {
        public string Id, Name, Path, Browser;
    }
    public sealed class CleanupResult
    {
        public List<Candidate> Files = new List<Candidate>();
        public List<Category> Categories = new List<Category>();
        public WalkStats Stats = new WalkStats();
        public List<string> Notes = new List<string>();
    }
    public sealed class DeleteResult
    {
        public int Deleted, Skipped;
        public long Bytes;
        public List<string> Errors = new List<string>();
        public List<string> DeletedPaths = new List<string>();
        public bool Canceled;
    }

    public sealed class CleanerEngine
    {
        private readonly List<CleanupRoot> roots;
        private readonly Settings settings;
        public CleanerEngine(Settings settings, IEnumerable<CleanupRoot> roots)
        {
            this.settings = settings; settings.Validate();
            this.roots = roots.ToList();
            foreach (CleanupRoot root in this.roots)
            {
                root.Path = Paths.Full(root.Path);
                if (root.Path.Length < 4 || Paths.Same(root.Path, System.IO.Path.GetPathRoot(root.Path)))
                    throw new InvalidOperationException("Нельзя очищать корень диска: " + root.Path);
            }
        }
        public static List<CleanupRoot> DefaultRoots()
        {
            var result = new List<CleanupRoot>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            result.Add(new CleanupRoot { Id = "temp", Name = "Временные файлы приложений", Path = System.IO.Path.Combine(local, "Temp") });
            string systemTemp = System.IO.Path.GetTempPath();
            if (!Paths.Same(systemTemp, result[0].Path) && !Paths.Same(systemTemp, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
                result.Add(new CleanupRoot { Id = "temp", Name = "Временные файлы приложений", Path = systemTemp });
            result.Add(new CleanupRoot { Id = "windows", Name = "Временные файлы Windows", Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") });
            foreach (string browser in new[] { "chrome", "msedge" })
            {
                string basePath = System.IO.Path.Combine(local, browser == "chrome" ? @"Google\Chrome\User Data" : @"Microsoft\Edge\User Data");
                if (!Directory.Exists(basePath)) continue;
                try
                {
                    foreach (string profile in Directory.EnumerateDirectories(basePath))
                    {
                        string name = System.IO.Path.GetFileName(profile);
                        if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                        if ((File.GetAttributes(profile) & FileAttributes.ReparsePoint) != 0) continue;
                        foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache" })
                            result.Add(new CleanupRoot { Id = "browser", Name = "Кэш Edge и Chrome", Browser = browser, Path = System.IO.Path.Combine(profile, cache) });
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return result.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }
        public CleanupResult Scan(CancellationToken cancel, bool includeRecycleBin)
        {
            var result = new CleanupResult();
            DateTime cutoff = DateTime.UtcNow.AddDays(-settings.MinimumAgeDays);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CleanupRoot root in roots)
            {
                Category category = result.Categories.FirstOrDefault(c => c.Id == root.Id);
                if (category == null)
                {
                    category = new Category { Id = root.Id, Name = root.Name, Selected = root.Id != "browser", Note = "Старше " + settings.MinimumAgeDays + " дней" };
                    result.Categories.Add(category);
                }
                if (root.Browser != null && BrowserRunning(root.Browser))
                { category.Note = "Пропущено: браузер работает"; continue; }
                FileWalker.Walk(root.Path, settings, cancel, result.Stats, delegate(FileInfo file)
                {
                    if (file.LastWriteTimeUtc > cutoff || !seen.Add(file.FullName)) return;
                    long bytes = file.Length;
                    result.Files.Add(new Candidate { CategoryId = root.Id, Path = file.FullName, Root = root.Path, Bytes = bytes, ModifiedUtc = file.LastWriteTimeUtc, Selected = category.Selected });
                    category.Bytes += bytes; category.Count++;
                }, null);
                if (result.Stats.Limited) break;
            }
            if (includeRecycleBin)
            {
                long bytes, count; string error;
                if (RecycleBin.Query(out bytes, out count, out error)) result.Categories.Add(new Category { Id = "recycle", Name = "Корзина", Bytes = bytes, Count = count, Note = "Все диски · отдельное подтверждение", Selected = false });
                else result.Notes.Add("Корзина: " + error);
            }
            if (result.Stats.Limited) result.Notes.Add("Достигнут предел проверки. Показаны частичные результаты.");
            return result;
        }
        public DeleteResult Delete(IEnumerable<Candidate> selected, CancellationToken cancel)
        {
            var result = new DeleteResult();
            foreach (Candidate file in selected)
            {
                if (cancel.IsCancellationRequested) { result.Canceled = true; break; }
                try
                {
                    CleanupRoot root = roots.FirstOrDefault(r => r.Id == file.CategoryId && Paths.Same(r.Path, file.Root));
                    if (root == null || !Paths.Within(file.Path, root.Path)) throw new IOException("Путь вне разрешённой папки.");
                    if (root.Browser != null && BrowserRunning(root.Browser)) throw new IOException("Браузер запущен.");
                    if (!Paths.OrdinaryChain(file.Path, root.Path)) throw new IOException("Обнаружена ссылка или junction.");
                    NativeDelete.CheckedDelete(file, DateTime.UtcNow.AddDays(-settings.MinimumAgeDays));
                    result.Deleted++; result.Bytes += file.Bytes; result.DeletedPaths.Add(file.Path);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is System.ComponentModel.Win32Exception) && !(ex is ArgumentException)) throw;
                    result.Skipped++;
                    if (result.Errors.Count < 100) result.Errors.Add(file.Path + " — " + ex.Message);
                }
            }
            return result;
        }
        private static bool BrowserRunning(string name)
        {
            Process[] processes = Process.GetProcessesByName(name);
            try { return processes.Length != 0; }
            finally { foreach (Process p in processes) p.Dispose(); }
        }
    }

    internal static class NativeDelete
    {
        [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref Disposition info, uint size);
        public static void CheckedDelete(Candidate file, DateTime cutoff)
        {
            // Holding the handle denies writers and renames while validating and deleting this exact file.
            using (SafeFileHandle handle = CreateFile(file.Path, 0x00010000 | 0x00000080, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                FileInfoNative info;
                if (!GetFileInformationByHandle(handle, out info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0) throw new IOException("Объект является ссылкой или каталогом.");
                long bytes = ((long)info.SizeHigh << 32) | info.SizeLow;
                long ticks = ((long)(uint)info.Written.dwHighDateTime << 32) | (uint)info.Written.dwLowDateTime;
                DateTime modified = DateTime.FromFileTimeUtc(ticks);
                if (bytes != file.Bytes || modified != file.ModifiedUtc || modified > cutoff) throw new IOException("Файл изменился после проверки.");
                var name = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, 0);
                if (length == 0 || length >= name.Capacity) throw new IOException("Не удалось проверить конечный путь файла.");
                string finalPath = name.ToString();
                if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) finalPath = @"\\" + finalPath.Substring(8);
                else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal)) finalPath = finalPath.Substring(4);
                if (!Paths.Same(finalPath, file.Path)) throw new IOException("Конечный путь отличается от проверенного.");
                var disposition = new Disposition { Delete = true };
                if (!SetFileInformationByHandle(handle, 4, ref disposition, 4)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    public static class RecycleBin
    {
        [StructLayout(LayoutKind.Sequential, Pack = 8)] private struct QueryInfo { public uint Size; public long Bytes, Items; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string root, ref QueryInfo info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr owner, string root, uint flags);
        public static bool Query(out long bytes, out long items, out string error)
        {
            var info = new QueryInfo { Size = (uint)Marshal.SizeOf(typeof(QueryInfo)) };
            int hr = SHQueryRecycleBin(null, ref info);
            bytes = hr >= 0 ? info.Bytes : 0; items = hr >= 0 ? info.Items : 0;
            error = hr < 0 ? "HRESULT 0x" + hr.ToString("X8") : null;
            return hr >= 0;
        }
        public static void Empty()
        {
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, 1 | 2 | 4);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
    }

    public sealed class LargeFileResult
    {
        public List<Candidate> Files = new List<Candidate>();
        public WalkStats Stats = new WalkStats();
        public int Matched;
    }
    public static class LargeFiles
    {
        public static LargeFileResult Scan(string directory, Settings settings, CancellationToken cancel)
        {
            string root = System.IO.Path.GetFullPath(directory);
            var result = new LargeFileResult();
            long threshold = (long)settings.LargeFileMinimumMB * 1024 * 1024;
            long smallest = long.MaxValue;
            FileWalker.Walk(root, settings, cancel, result.Stats, delegate(FileInfo file)
            {
                long size = file.Length;
                if (size < threshold) return;
                result.Matched++;
                if (result.Files.Count >= settings.LargeFileResultLimit && size <= smallest) return;
                result.Files.Add(new Candidate { Path = file.FullName, Bytes = size, ModifiedUtc = file.LastWriteTimeUtc });
                if (result.Files.Count > settings.LargeFileResultLimit) result.Files = result.Files.OrderByDescending(f => f.Bytes).Take(settings.LargeFileResultLimit).ToList();
                smallest = result.Files.Min(f => f.Bytes);
            }, delegate(string path)
            {
                string name = System.IO.Path.GetFileName(path);
                return name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase);
            });
            result.Files = result.Files.OrderByDescending(f => f.Bytes).ToList();
            return result;
        }
    }
}
