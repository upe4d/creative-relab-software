using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PCDrivers
{
    internal sealed class Suite : IDisposable
    {
        internal readonly object Gate = new object(); internal bool Busy;
        internal Suite(Store store) { }
        public void Dispose() { }
    }
    internal sealed class Api
    {
        private readonly Store store; private readonly Action stop; private readonly Suite suite;
        private List<Device> devices = new List<Device>();
        internal Api(Store store, Action stop, Suite suite) { this.store = store; this.stop = stop; this.suite = suite; }
        internal object Dispatch(Dictionary<string, object> input)
        {
            string action = Store.Text(input, "action");
            if (action == "ping") return Store.Map("product", "PC Drivers", "version", "0.1.0 Preview");
            if (action == "stop") { stop(); return true; }
            lock (suite.Gate)
            {
                suite.Busy = true;
                try
                {
                    if (action == "scan") { devices = Devices.Scan(); store.Write("inventory.json", Store.Map("time", DateTimeOffset.Now.ToString("o"), "devices", devices)); return devices; }
                    if (action == "match") { Device d = Selected(input); return Devices.Match(d.Id, Store.Text(input, "inf")); }
                    if (action == "report")
                    {
                        if (devices.Count == 0) throw new InvalidOperationException("Сначала прочитайте оборудование");
                        string path = Path.Combine(store.Data, "inventory-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".html");
                        File.WriteAllText(path, Report(devices), new UTF8Encoding(true)); return Store.Map("path", path, "devices", devices.Count);
                    }
                    if (action == "pickInf") return PickInf();
                    if (action == "backup") return Backup(Selected(input));
                    if (action == "backups") return Directory.GetDirectories(Path.Combine(store.Data, "Backups")).Select(p => Store.Map("name", Path.GetFileName(p), "path", p, "manifest", File.Exists(Path.Combine(p, "manifest.json")))).ToArray();
                    if (action == "openData") { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + store.Data + "\"") { UseShellExecute = true }); return true; }
                    throw new ArgumentException("Неизвестная команда");
                }
                finally { suite.Busy = false; }
            }
        }
        private Device Selected(Dictionary<string, object> input) { Device d = devices.FirstOrDefault(x => x.Id == Store.Text(input, "id")); if (d == null) throw new InvalidOperationException("Выберите устройство из актуального списка"); return d; }
        internal object Backup(Device d)
        {
            if (!Regex.IsMatch(d.Inf ?? "", @"\Aoem[0-9]+\.inf\z", RegexOptions.IgnoreCase)) throw new InvalidOperationException("Экспорт доступен для сторонних пакетов oem*.inf. Встроенные драйверы Windows этим способом не экспортируются");
            string path = Path.Combine(store.Data, "Backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(path);
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "pnputil.exe"), "/export-driver " + d.Inf + " \"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(psi))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60000)) { process.Kill(); throw new TimeoutException("Экспорт превысил 60 секунд. Частичные файлы сохранены: " + path); }
                string log = output.Result + "\r\n" + error.Result; File.WriteAllText(Path.Combine(path, "export.log"), log);
                if (process.ExitCode != 0) throw new IOException("PnPUtil: код " + process.ExitCode + ". Журнал: " + path);
            }
            var files = new List<object>(); long total = 0;
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "export.log") continue;
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Экспорт содержит файловую ссылку");
                using (Stream s = File.OpenRead(file)) files.Add(Store.Map("path", file.Substring(path.Length + 1), "bytes", s.Length, "sha256", Store.Hash(s)));
                total += new FileInfo(file).Length;
            }
            if (!Directory.GetFiles(path, "*.inf", SearchOption.AllDirectories).Any() || files.Count == 0) throw new IOException("PnPUtil завершился без пакета INF");
            var manifest = Store.Map("created", DateTimeOffset.Now.ToString("o"), "inf", d.Inf, "device", d.Id, "files", files, "bytes", total);
            Store.WritePath(Path.Combine(path, "manifest.json"), manifest); return Store.Map("path", path, "files", files.Count, "bytes", total);
        }
        internal static string Report(List<Device> rows)
        {
            Func<string, string> e = s => System.Security.SecurityElement.Escape(s ?? "");
            var h = new StringBuilder("<!doctype html><html lang=ru><meta charset=utf-8><title>PC Drivers · Оборудование</title><style>body{font:15px Segoe UI;margin:28px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccd4df;padding:10px;text-align:left;overflow-wrap:anywhere}</style><h1>Оборудование PC Drivers</h1><p>" + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " · устройств: " + rows.Count + "</p><table><tr><th>Устройство</th><th>Статус</th><th>Поставщик / версия</th><th>INF / Hardware ID</th></tr>");
            foreach (Device d in rows) h.Append("<tr><td>" + e(d.Name) + "</td><td>" + e(d.Status + " " + d.ReadError) + "</td><td>" + e(d.Provider + " / " + d.Version) + "</td><td>" + e(d.Inf) + "<br>" + e(String.Join("; ", d.HardwareIds)) + "</td></tr>");
            return h.Append("</table></html>").ToString();
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int max);
        private sealed class Owner : IWin32Window { public IntPtr Handle { get; set; } }
        private static string PickInf()
        {
            string path = ""; Exception error = null;
            var t = new Thread(delegate()
            {
                try
                {
                    IntPtr foreground = GetForegroundWindow(); var title = new StringBuilder(512); GetWindowTextW(foreground, title, title.Capacity);
                    using (var fallback = new Form { Width = 1, Height = 1, Opacity = 0, ShowInTaskbar = false, TopMost = true })
                    using (var dialog = new OpenFileDialog { Filter = "Драйверы Windows (*.inf)|*.inf", Title = "Выберите пакет INF для проверки совместимости", CheckFileExists = true, Multiselect = false })
                    {
                        IWin32Window owner;
                        if (title.ToString().Contains("PC Drivers")) owner = new Owner { Handle = foreground }; else { fallback.Show(); fallback.Activate(); owner = fallback; }
                        if (dialog.ShowDialog(owner) == DialogResult.OK) path = dialog.FileName;
                    }
                }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join(); if (error != null) throw error; return path;
        }
    }
}
