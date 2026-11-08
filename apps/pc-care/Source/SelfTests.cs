using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PCCare
{
    public static class SelfTests
    {
        private static void Check(bool condition, string label, List<string> checks)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + label);
            checks.Add("PASS: " + label);
        }
        private static void Throws(Action action, string label, List<string> checks)
        {
            bool failed = false;
            try { action(); } catch (IOException) { failed = true; } catch (InvalidOperationException) { failed = true; }
            Check(failed, label, checks);
        }
        public static int Run(string baseDirectory)
        {
            string root = Path.Combine(baseDirectory, "Data", "SelfTests", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            var checks = new List<string>();
            string testKey = @"Software\PCCare\Tests\" + Guid.NewGuid().ToString("N") + @"\Run";
            try
            {
                var settings = new Settings();
                Check(StartupManager.DecodeApproval(null) == true && StartupManager.DecodeApproval(new byte[] { 2,0,0,0,0,0,0,0,0,0,0,0 }) == true, "Missing approval and enabled Windows status recognized", checks);
                Check(StartupManager.DecodeApproval(new byte[] { 1,0,0,0,0,0,0,0,0,0,0,0 }) == false && StartupManager.DecodeApproval(new byte[] { 3,0,0,0,0,0,0,0,0,0,0,0 }) == false, "Windows disabled statuses recognized", checks);
                Check(StartupManager.DecodeApproval(new byte[] { 6,0,0,0,0,0,0,0,0,0,0,0 }) == true && StartupManager.DecodeApproval(new byte[] { 7,0,0,0,0,0,0,0,0,0,0,0 }) == false, "Alternate Windows startup statuses recognized", checks);
                Check(StartupManager.DecodeApproval(new byte[] { 2 }) == null && StartupManager.DecodeApproval(new byte[] { 99,0,0,0,0,0,0,0,0,0,0,0 }) == null, "Malformed and unknown approval flags remain unknown", checks);
                string temp = Path.Combine(root, "Temp"), outside = Path.Combine(root, "Outside");
                Directory.CreateDirectory(temp); Directory.CreateDirectory(outside);
                string old = Path.Combine(temp, "old.tmp"), fresh = Path.Combine(temp, "fresh.tmp"), locked = Path.Combine(temp, "locked.tmp"), changed = Path.Combine(temp, "changed.tmp"), keep = Path.Combine(outside, "keep.txt");
                foreach (string path in new[] { old, fresh, locked, changed, keep }) File.WriteAllText(path, "PC Care isolated fixture");
                foreach (string path in new[] { old, locked, changed, keep }) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
                var cleaner = new CleanerEngine(settings, new[] { new CleanupRoot { Id = "test", Name = "Test", Path = temp } });
                CleanupResult scan = cleaner.Scan(CancellationToken.None, false);
                Check(scan.Files.Count == 3 && !scan.Files.Any(f => f.Path == fresh), "Fresh files excluded; three old files found", checks);
                File.SetLastWriteTimeUtc(changed, DateTime.UtcNow);
                using (FileStream held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    DeleteResult deleted = cleaner.Delete(scan.Files, CancellationToken.None);
                    Check(deleted.Deleted == 1 && deleted.Skipped == 2 && !File.Exists(old), "Old file deleted; locked and changed files preserved", checks);
                }
                Check(File.Exists(fresh) && File.Exists(locked) && File.Exists(changed), "All excluded files still exist", checks);
                var escape = new Candidate { CategoryId = "test", Root = temp, Path = Path.Combine(temp, "..", "Outside", "keep.txt"), Bytes = new FileInfo(keep).Length, ModifiedUtc = File.GetLastWriteTimeUtc(keep) };
                Check(cleaner.Delete(new[] { escape }, CancellationToken.None).Skipped == 1 && File.Exists(keep), "Traversal outside cleanup root rejected", checks);
                string link = Path.Combine(temp, "outside-link");
                var psi = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + outside + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); Check(p.ExitCode == 0, "Isolated directory junction created", checks); }
                CleanupResult withLink = cleaner.Scan(CancellationToken.None, false);
                Check(!withLink.Files.Any(f => f.Path.IndexOf("outside-link", StringComparison.OrdinalIgnoreCase) >= 0), "Junction not traversed by scanner", checks);
                var throughLink = new Candidate { CategoryId = "test", Root = temp, Path = Path.Combine(link, "keep.txt"), Bytes = new FileInfo(keep).Length, ModifiedUtc = File.GetLastWriteTimeUtc(keep) };
                Check(cleaner.Delete(new[] { throughLink }, CancellationToken.None).Skipped == 1 && File.Exists(keep), "Delete through junction rejected", checks);
                var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                Check(cleaner.Delete(scan.Files, cancellation.Token).Canceled, "Cleanup cancellation preserves remaining files", checks);
                Throws(() => new CleanerEngine(settings, new[] { new CleanupRoot { Id = "bad", Path = Path.GetPathRoot(root) } }), "Drive root cannot be configured for cleanup", checks);
                string many = Path.Combine(root, "Many"); Directory.CreateDirectory(many);
                for (int i = 0; i < 130; i++) File.WriteAllText(Path.Combine(many, i.ToString() + ".txt"), "x");
                var bounded = new Settings { ScanFileLimit = 100, ScanEntryLimit = 100 };
                WalkStats stats = new WalkStats(); FileWalker.Walk(many, bounded, CancellationToken.None, stats, f => { }, null);
                Check(stats.Limited && stats.Files == 100, "Enumeration limit produces partial-result status", checks);
                var store = new AppStore(Path.Combine(root, "Store"));
                string startupFolder = Path.Combine(root, "Startup"); Directory.CreateDirectory(startupFolder);
                string startupFile = Path.Combine(startupFolder, "fixture.lnk"); File.WriteAllText(startupFile, "fixture shortcut; never executed");
                Registry.CurrentUser.CreateSubKey(testKey).Dispose();
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(testKey, true)) key.SetValue("Fixture", @"%TEMP%\pccare-fixture.exe --example", RegistryValueKind.ExpandString);
                var locations = new[] { new RegistryLocation { Hive = "HKCU", View = RegistryView.Default, Key = testKey } };
                var manager = new StartupManager(store, locations, new[] { startupFolder });
                StartupEntry registryEntry = manager.List().Entries.Single(e => e.Kind == "Registry");
                manager.Disable(registryEntry);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(testKey)) Check(key.GetValue("Fixture") == null, "Isolated Run entry disabled after backup persisted", checks);
                var reopened = new AppStore(store.Directory);
                var recoveredManager = new StartupManager(reopened, locations, new[] { startupFolder });
                Check(recoveredManager.List().Entries.Any(e => e.Id == registryEntry.Id && !e.Enabled), "Disabled startup survives application restart", checks);
                StartupBackup registryBackup = reopened.State.Backups.Single(b => b.Id == registryEntry.Id); registryBackup.State = "Prepared"; reopened.Save();
                recoveredManager.Restore(registryEntry.Id);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(testKey)) Check(key.GetValueKind("Fixture") == RegistryValueKind.ExpandString && (string)key.GetValue("Fixture", null, RegistryValueOptions.DoNotExpandEnvironmentNames) == @"%TEMP%\pccare-fixture.exe --example", "Prepared backup restores exact raw value and registry type", checks);
                registryEntry = recoveredManager.List().Entries.Single(e => e.Kind == "Registry"); recoveredManager.Disable(registryEntry);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(testKey, true)) key.SetValue("Fixture", "External replacement", RegistryValueKind.String);
                Throws(() => recoveredManager.Restore(registryEntry.Id), "Registry restore refuses to overwrite external change", checks);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(testKey, true)) { Check((string)key.GetValue("Fixture") == "External replacement", "Conflicting registry value preserved", checks); key.DeleteValue("Fixture"); }
                recoveredManager.Restore(registryEntry.Id);
                StartupEntry folderEntry = recoveredManager.List().Entries.Single(e => e.Kind == "Folder"); recoveredManager.Disable(folderEntry);
                Check(!File.Exists(startupFile) && File.Exists(reopened.State.Backups.Single(b => b.Id == folderEntry.Id).SavedPath), "Startup folder file backed up on D before removal", checks);
                File.WriteAllText(startupFile, "External replacement");
                Throws(() => recoveredManager.Restore(folderEntry.Id), "Startup file restore refuses collision", checks);
                Check(File.ReadAllText(startupFile) == "External replacement", "Conflicting startup file preserved", checks);
                File.Delete(startupFile); recoveredManager.Restore(folderEntry.Id);
                Check(File.ReadAllText(startupFile) == "fixture shortcut; never executed", "Startup file restored byte for byte", checks);
                Check(new AppStore(store.Directory).State.History.Count >= 6, "Operation history persists", checks);
                string large = Path.Combine(root, "Large"); Directory.CreateDirectory(large);
                using (FileStream f = File.Create(Path.Combine(large, "small.bin"))) f.SetLength(100);
                using (FileStream f = File.Create(Path.Combine(large, "large.bin"))) f.SetLength(2 * 1024 * 1024);
                LargeFileResult largeResult = LargeFiles.Scan(large, new Settings { LargeFileMinimumMB = 1 }, CancellationToken.None);
                Check(largeResult.Files.Count == 1 && largeResult.Files[0].Bytes == 2 * 1024 * 1024, "Large-file threshold returns actual file size", checks);
                var monitor = new SystemMonitor(); monitor.Read(); Thread.Sleep(150); SystemSample sample = monitor.Read();
                Check(sample.MemoryAvailable && sample.TotalMemory > sample.UsedMemory && sample.Disks.Count > 0 && sample.CpuPercent.HasValue, "Live CPU, physical RAM and disk data available", checks);
                Check(new AppStore(store.Directory).State.Backups.All(b => b.State == "Restored"), "All test startup records restored", checks);
                File.WriteAllLines(Path.Combine(root, "results.txt"), checks.Concat(new[] { "PASS: " + checks.Count + " checks; only isolated fixtures changed." }));
                File.WriteAllText(Path.Combine(baseDirectory, "Data", "self-test-latest.txt"), root + Environment.NewLine + string.Join(Environment.NewLine, checks));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllLines(Path.Combine(root, "results.txt"), checks.Concat(new[] { ex.ToString() }));
                File.WriteAllText(Path.Combine(baseDirectory, "Data", "self-test-latest.txt"), root + Environment.NewLine + ex);
                return 1;
            }
            finally
            {
                string parent = testKey.Substring(0, testKey.LastIndexOf('\\'));
                if (parent.StartsWith(@"Software\PCCare\Tests\", StringComparison.Ordinal) && parent.Length == @"Software\PCCare\Tests\".Length + 32)
                    Registry.CurrentUser.DeleteSubKeyTree(parent, false);
            }
        }
        public static async Task UiSmoke(Window window, MainController controller, AppStore store)
        {
            if (store.Settings.Theme != "Dark") controller.Get<Button>("ThemeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var result = new CleanupResult();
            result.Categories.Add(new Category { Id = "test", Name = "Тестовая категория", Bytes = 2048, Count = 1, Selected = true, Note = "Изолированные данные" });
            result.Files.Add(new Candidate { CategoryId = "test", Path = Path.Combine(store.Directory, "fixture.tmp"), Bytes = 2048, ModifiedUtc = DateTime.UtcNow.AddDays(-10), Selected = true });
            controller.LoadCleanup(result);
            var checks = new List<string>();
            foreach (string page in new[] { "Overview", "Cleanup", "Startup", "Large", "History" })
            {
                controller.SwitchPage(page); window.UpdateLayout();
                Check(controller.Get<Grid>("Page" + page).Visibility == Visibility.Visible, "Native screen: " + page, checks);
            }
            controller.SwitchPage("Cleanup"); result.Files[0].Selected = false;
            Check(controller.Get<TextBlock>("SelectionText").Text.Contains("0 Б"), "Individual selection updates cleanup total", checks);
            result.Categories[0].Selected = false; result.Categories[0].Selected = true;
            Check(result.Files[0].Selected && controller.Get<Button>("CleanButton").IsEnabled, "Category selection updates files and clean action", checks);
            controller.Get<Button>("ThemeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(store.Settings.Theme == "Light", "Native light theme switches and saves", checks);
            Check(((SolidColorBrush)controller.Get<Border>("CaptionBar").Background).Color == Colors.White, "Title bar follows light application theme", checks);
            Check(((SolidColorBrush)controller.Get<TextBlock>("CaptionTitle").Foreground).Color == Color.FromRgb(0x18,0x21,0x2C), "Light title-bar text color applied", checks);
            controller.Get<Button>("ThemeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((SolidColorBrush)controller.Get<Border>("CaptionBar").Background).Color == Color.FromRgb(0x18,0x1D,0x24), "Title bar follows dark application theme", checks);
            Check(((SolidColorBrush)controller.Get<TextBlock>("CaptionTitle").Foreground).Color == Color.FromRgb(0xED,0xF0,0xF4), "Dark title-bar text color applied", checks);
            Check(window.WindowStyle == WindowStyle.None && System.Windows.Shell.WindowChrome.GetWindowChrome(window).CaptionHeight == 36, "Themed title bar replaces the external system caption", checks);
            Check(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(controller.Get<Button>("CaptionClose")), "Caption buttons remain interactive in the draggable title region", checks);
            await TableUiTests.Run(window, controller, checks);
            controller.SwitchPage("Overview");
            controller.Get<TextBlock>("CpuText").Text = "UI-проверка";
            controller.Get<TextBlock>("RamText").Text = "UI-проверка";
            controller.Get<TextBlock>("DiskText").Text = "UI-проверка";
            controller.Get<TextBlock>("FoundText").Text = "2 КБ · тестовые данные";
            await Task.Delay(250); window.UpdateLayout();
            SaveWindow(window, Path.Combine(store.Directory, "overview.png"));
            controller.SwitchPage("Cleanup"); window.UpdateLayout();
            SaveWindow(window, Path.Combine(store.Directory, "cleanup.png"));
            File.WriteAllLines(Path.Combine(store.Directory, "ui-smoke-results.txt"), checks);
        }
        internal static void SaveWindow(Window window, string path)
        {
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (DrawingContext drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            bitmap.Render(background);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream file = File.Create(path)) encoder.Save(file);
        }
        public static async Task CaptureLivePreview(Window window, MainController controller, AppStore store)
        {
            for (int i = 0; i < 20 && !controller.Get<TextBlock>("CpuText").Text.Contains("%"); i++) await Task.Delay(500);
            if (!controller.Get<TextBlock>("CpuText").Text.Contains("%") || controller.Get<DataGrid>("DiskGrid").Items.Count == 0)
                throw new InvalidOperationException("Live values did not reach the native overview.");
            string directory = Path.Combine(store.Directory, "Verification"); Directory.CreateDirectory(directory);
            await Task.Delay(200);
            SaveWindow(window, Path.Combine(directory, "window-with-titlebar.png"));
            window.UpdateLayout(); SaveWindow(window, Path.Combine(directory, "overview-live.png"));
            controller.LoadStartup(StartupManager.Default(store).List());
            controller.SwitchPage("Startup"); await Task.Delay(300); window.UpdateLayout();
            await TableUiTests.ClickHeader(controller.Get<DataGrid>("StartupGrid"), "StatusOrder");
            File.WriteAllLines(Path.Combine(directory, "startup-layout.txt"), controller.Get<DataGrid>("StartupGrid").Columns.Select(c => Convert.ToString(c.Header) + " | " + c.ActualWidth + " | " + c.Visibility + " | " + c.DisplayIndex));
            SaveWindow(window, Path.Combine(directory, "startup-live.png"));
            controller.Get<Button>("ThemeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100); window.UpdateLayout();
            SaveWindow(window, Path.Combine(directory, "startup-live-light.png"));
            controller.Get<Button>("ThemeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            File.WriteAllText(Path.Combine(directory, "ui-live-result.txt"), "PASS: Actual CPU, RAM and disk values displayed by the native application. No cleanup or startup change triggered.");
        }
    }
}
