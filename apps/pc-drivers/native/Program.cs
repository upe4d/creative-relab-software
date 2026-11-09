using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace PCDrivers
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (!String.Equals(Path.GetPathRoot(root), @"D:\", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show("Установи PC Drivers на диск D.", "PC Drivers"); return 1; }
            bool headless = args.Contains("--headless") || args.Contains("--self-test") || args.Contains("--suite-test") || args.Contains("--tray-test");
            bool agent=args.Contains("--agent");
            try
            {
                var store = new Store(root);
                if (args.Contains("--self-test")) return SelfTests.Run(store);

                if(args.Contains("--tray-test")){using(var testTray=new Tray(delegate{},delegate{})){if(!testTray.Initialized)throw new IOException("Не создан значок трея.");Thread.Sleep(1000);store.Write("tray-test.json",Store.Map("initialized",true));}return 0;}
                string mutexName;using(var nameBytes=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant())))mutexName=@"Local\PCDrivers-Native-"+Store.Hash(nameBytes).Substring(0,20);
                using (var mutex = new Mutex(false, mutexName))
                {
                    bool owner; try { owner = mutex.WaitOne(0); } catch (AbandonedMutexException) { owner = true; }
                    if (!owner)
                    {
                        var running = store.Read("runtime.json"); if (!headless&&!agent) Browser(root, Convert.ToInt32(Store.Get(running, "port")), Store.Text(running, "token")); return 0;
                    }
                    try
                    {
                        byte[] random = new byte[32]; using (var generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
                        string token = BitConverter.ToString(random).Replace("-", "").ToLowerInvariant();
                        using (var server = new Server(store, token))
                        {
                            server.Start(); var runtime = Store.Map("port", server.Port, "token", token, "pid", Process.GetCurrentProcess().Id, "started", DateTimeOffset.Now.ToString("o"));
                            store.Write("runtime.json", runtime); if (headless) store.Write("test-runtime.json", runtime);
                            using(var tray=headless?null:new Tray(delegate{Browser(root,server.Port,token);},server.RequestStop))
                            {
                                if (!headless&&!agent) Browser(root, server.Port, token);
                                server.Wait();
                            }
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception error)
            {
                try { Directory.CreateDirectory(Path.Combine(root, "Data")); Store.WritePath(Path.Combine(root,"Data","launch-errors.json"),Store.Map("date",DateTimeOffset.Now.ToString("o"),"error",error.ToString())); } catch (Exception) { }
                if (!headless) MessageBox.Show(error.Message, "PC Drivers"); return 1;
            }
        }
        private static void Browser(string root, int port, string token)
        {
            string[] candidates = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe") };
            string browser = candidates.FirstOrDefault(File.Exists); if (browser == null) throw new IOException("Для окна PC Drivers требуется установленный Microsoft Edge или Google Chrome.");
            string profile = Path.Combine(root, "Data", "Browser");
            Process.Start(new ProcessStartInfo(browser, "--app=http://127.0.0.1:" + port + "/#" + token + " --user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check --disable-background-networking --disable-sync --force-dark-mode") { UseShellExecute = false });
        }
    }
}
