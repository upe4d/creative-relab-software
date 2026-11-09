using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace PCCare
{
    public static class RegistryTests
    {
        public static int Run(string directory)
        {
            string root = RegistryAudit.TestPrefix + Guid.NewGuid().ToString("N");
            string work = Path.Combine(directory, "Data", "RegistryTests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(work); var checks = new List<string>();
            Action<bool, string> check = (ok, name) => { if (!ok) throw new InvalidDataException(name); checks.Add("PASS " + name); };
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
            try
            {
                var audit = new RegistryAudit(Path.Combine(work, "Backups"), root, view);
                string missing = Path.Combine(work, "absent.exe"), exists = Path.Combine(work, "present.exe"); File.WriteAllText(exists, "own inert fixture");
                using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                {
                    using (RegistryKey run = hive.CreateSubKey(root + @"\Run"))
                    {
                        run.SetValue("Missing", "\"" + missing + "\" --test", RegistryValueKind.ExpandString);
                        run.SetValue("Present", "\"" + exists + "\"", RegistryValueKind.String);
                        run.SetValue("Ambiguous", @"D:\Program Files\Unknown\a.exe --arg");
                        run.SetValue("Offline", @"Z:\offline.exe");
                    }
                    using (RegistryKey app = hive.CreateSubKey(root + @"\App Paths\test.exe")) { app.SetValue("", missing); app.SetValue("Keep", "preserve"); }
                    using (RegistryKey mui = hive.CreateSubKey(root + @"\MuiCache")) { mui.SetValue(missing + ".FriendlyAppName", "<script>bad</script>"); mui.SetValue("Keep", "preserve"); }
                    List<RegistryFinding> scan = audit.Scan(CancellationToken.None);
                    check(scan.Count(f => f.Eligible) == 3, "Three confirmed missing references eligible");
                    check(!scan.Any(f => f.ValueName == "Present"), "Existing target omitted");
                    check(scan.Single(f => f.ValueName == "Ambiguous").Eligible == false, "Ambiguous command preserved");
                    check(scan.Single(f => f.ValueName == "Offline").Eligible == false, "Unavailable volume preserved");
                    check(RegistryAudit.MissingReason(exists) == null, "Existing file distinguished from missing");
                    check(RegistryAudit.MissingReason(@"\\offline\share\a.exe") != "Файл отсутствует", "Network paths never eligible");
                    string report = audit.Report(scan); check(File.Exists(report) && new FileInfo(report).Length > 300, "Actual HTML report created");
                    RegistryBackup b = audit.Fix(scan.Where(f => f.Eligible), CancellationToken.None);
                    check(b.Items.Count(i => i.State == "Removed") == 3, "Selected three values removed with backup");
                    check(audit.Load(b.Path).Items.Count == 3, "Backup checksum and scope verified");
                    using (RegistryKey app = hive.OpenSubKey(root + @"\App Paths\test.exe")) check((string)app.GetValue("Keep") == "preserve", "Neighbour values and section retained");
                    using (RegistryKey run = hive.OpenSubKey(root + @"\Run", true)) run.SetValue("Missing", "new owner value");
                    b = audit.Restore(b.Path, CancellationToken.None);
                    check(b.Items.Count(i => i.State == "Restored") == 2 && b.Items.Count(i => i.State == "Conflict") == 1, "Rollback restores values and detects conflict");
                    using (RegistryKey run = hive.OpenSubKey(root + @"\Run", true)) { check((string)run.GetValue("Missing") == "new owner value", "Conflict never overwritten"); run.DeleteValue("Missing"); }
                    b = audit.Restore(b.Path, CancellationToken.None);
                    using (RegistryKey run = hive.OpenSubKey(root + @"\Run")) check(run.GetValueKind("Missing") == RegistryValueKind.ExpandString && (string)run.GetValue("Missing", null, RegistryValueOptions.DoNotExpandEnvironmentNames) == "\"" + missing + "\" --test", "Exact type and original command restored");
                    check(audit.Restore(b.Path, CancellationToken.None).Items.All(i => i.State == "Restored"), "Repeated rollback idempotent");
                    scan = audit.Scan(CancellationToken.None);
                    using (RegistryKey run = hive.OpenSubKey(root + @"\Run", true)) run.SetValue("Missing", "changed after scan");
                    b = audit.Fix(scan.Where(f => f.ValueName == "Missing"), CancellationToken.None);
                    check(b.Items.Single().State == "Skipped", "Changed value skipped at fix time");
                    scan = audit.Scan(CancellationToken.None);
                    File.WriteAllText(missing, "appeared");
                    b = audit.Fix(scan.Where(f => f.Category == "Пути приложений"), CancellationToken.None);
                    check(b.Items.Single().State == "Skipped", "Reappeared file prevents removal");
                    var cancel = new CancellationTokenSource(); cancel.Cancel();
                    b = audit.Fix(scan.Where(f => f.Category == "Кэш названий"), cancel.Token);
                    check(b.Items.Single().State == "Prepared", "Cancellation retains prepared backup without removal");
                    RegistryEnvelope env = RegistryAudit.Deserialize<RegistryEnvelope>(File.ReadAllBytes(b.Path)); env.Sha256 = new string('0', 64); File.WriteAllBytes(b.Path, RegistryAudit.Serialize(env));
                    bool rejected = false; try { audit.Load(b.Path); } catch (InvalidDataException) { rejected = true; }
                    check(rejected, "Corrupted checksum rejected before restore");
                    using (RegistryKey types = hive.CreateSubKey(root + @"\Types"))
                    {
                        var values = new object[] { "строка", "%TEMP%", new[] { "one", "два" }, 1234, 12345678901L, new byte[] { 0, 12, 255 } };
                        var kinds = new[] { RegistryValueKind.String, RegistryValueKind.ExpandString, RegistryValueKind.MultiString, RegistryValueKind.DWord, RegistryValueKind.QWord, RegistryValueKind.Binary };
                        for (int i = 0; i < kinds.Length; i++) { types.SetValue("a", values[i], kinds[i]); RegistryValueSnapshot v = RegistryValueSnapshot.Capture(types, "a"); types.SetValue("b", v.Value(), v.Kind); check(v.Same(RegistryValueSnapshot.Capture(types, "b")), "Snapshot round trip " + kinds[i]); }
                    }
                }
                File.WriteAllLines(Path.Combine(work, "results.txt"), checks); File.WriteAllText(Path.Combine(directory, "registry-tests-result.txt"), "PASS " + checks.Count + "\r\n" + work); return 0;
            }
            catch (Exception ex) { checks.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(work, "results.txt"), checks); File.WriteAllText(Path.Combine(directory, "registry-tests-result.txt"), "FAIL\r\n" + ex); return 1; }
            finally { using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view)) hive.DeleteSubKeyTree(root, false); }
        }
    }
}
