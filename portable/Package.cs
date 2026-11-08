using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
namespace CreativeRelab.Portable
{
    public sealed class Product { public string Name { get; set; } public string Slug { get; set; } public string Version { get; set; } public string MainExe { get; set; } public string SHA256 { get; set; } public string DefaultFolder { get; set; } }
    public static class Package
    {
        public static readonly Product Info = Load();
        private static Product Load() { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Relab.Product.json")) using (var r = new StreamReader(s)) return new JavaScriptSerializer().Deserialize<Product>(r.ReadToEnd()); }
        private static Stream Open() { return Assembly.GetExecutingAssembly().GetManifestResourceStream("Relab.Package.zip"); }
        public static string Hash(Stream s) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", ""); }
        public static string Target(string value)
        {
            var path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            if (!String.Equals(Path.GetPathRoot(path), @"D:\", StringComparison.OrdinalIgnoreCase) || path.Length < 5) throw new IOException("Выберите отдельную папку на диске D.");
            for (var current = path; !String.IsNullOrEmpty(current) && current.Length > 3; current = Path.GetDirectoryName(current)) if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Выберите папку без перенаправления на другой путь.");
            return path;
        }
        public static string EntryPath(string directory, string entry)
        {
            var relative = entry.Replace('/', '\\');
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new IOException("Недопустимый путь в архиве.");
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            if (!path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Путь выходит за выбранную папку.");
            for (var current = path; !String.IsNullOrEmpty(current) && current.Length > 3; current = Path.GetDirectoryName(current)) if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Путь содержит перенаправление.");
            return path;
        }
        public static int Verify()
        {
            using (var s = Open()) if (!String.Equals(Hash(s), Info.SHA256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Контрольная сумма встроенного архива не совпала.");
            using (var s = Open()) using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                if (!zip.Entries.Any(e => e.FullName == Info.MainExe)) throw new IOException("В архиве нет программы.");
                long total = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries) { EntryPath(@"D:\RelabValidation", e.FullName); if (!names.Add(e.FullName)) throw new IOException("Повтор пути в архиве."); total = checked(total + e.Length); }
                if (total > 1500000000L) throw new IOException("Архив превышает допустимый размер.");
                return zip.Entries.Count;
            }
        }
        // Обновляем только файлы пакета, сохраняя Data и резервируя заменяемые файлы на D.
        public static string Extract(string folder, Action<int> progress)
        {
            folder = Target(folder); Verify();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Info.MainExe))) using (p) { try { if (String.Equals(p.MainModule.FileName, Path.Combine(folder, Info.MainExe), StringComparison.OrdinalIgnoreCase)) throw new IOException("Сначала закройте " + Info.Name + "."); } catch (System.ComponentModel.Win32Exception) { } }
            var backup = Path.Combine(@"D:\Codex\portable-backups", Info.Slug, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            using (var s = Open()) using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                var index = 0;
                foreach (var entry in zip.Entries)
                {
                    var relative = entry.FullName.Replace('/', '\\');
                    if (relative.Equals("Data", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("Data\\", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = EntryPath(folder, entry.FullName);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(path); continue; }
                    if (File.Exists(path)) { var saved = EntryPath(backup, entry.FullName); Directory.CreateDirectory(Path.GetDirectoryName(saved)); File.Copy(path, saved, false); }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var input = entry.Open()) using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                    progress(++index * 100 / zip.Entries.Count);
                }
            }
            return Directory.Exists(backup) ? backup : null;
        }
        public static int SelfTest()
        {
            Verify(); var folder = Path.Combine(@"D:\Codex\work\CreativeRelab-Software\installer-tests", Info.Slug + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(folder, "Data"));
            File.WriteAllText(Path.Combine(folder, "Data", "preserve.txt"), "keep"); File.WriteAllText(Path.Combine(folder, Info.MainExe), "old");
            var backup = Extract(folder, delegate(int _) { });
            if (File.ReadAllText(Path.Combine(folder, "Data", "preserve.txt")) != "keep" || String.IsNullOrEmpty(backup) || File.ReadAllText(Path.Combine(backup, Info.MainExe)) != "old") throw new IOException("Не пройдена проверка сохранения данных.");
            foreach (var bad in new[] { "../escape.exe", @"C:\outside.exe", "ok.txt:stream" }) { bool refused = false; try { EntryPath(folder, bad); } catch (IOException) { refused = true; } if (!refused) throw new IOException("Не отвергнут опасный путь."); }
            bool cRefused = false; try { Target(@"C:\NotAllowed"); } catch (IOException) { cRefused = true; } if (!cRefused) throw new IOException("Не отвергнут диск C.");
            using (var zipStream = Open()) using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read)) using (var original = zip.GetEntry(Info.MainExe).Open()) using (var extracted = File.OpenRead(Path.Combine(folder, Info.MainExe))) if (Hash(original) != Hash(extracted)) throw new IOException("Файл после распаковки отличается.");
            var result = new { product = Info.Name, version = Info.Version, checks = 8, failures = 0, preservedData = true, backupVerified = true, traversalRejected = true, driveCRejected = true, extractedExecutableVerified = true, directory = folder };
            File.WriteAllText(Path.Combine(folder, "installer-results.json"), new JavaScriptSerializer().Serialize(result)); Console.WriteLine(new JavaScriptSerializer().Serialize(result)); return 0;
        }
    }
}