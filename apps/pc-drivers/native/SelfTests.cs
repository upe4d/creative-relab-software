using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCDrivers
{
    internal static class SelfTests
    {
        internal static int Run(Store store)
        {
            var checks = new List<string>();
            Action<bool, string> check = (ok, label) => { if (!ok) throw new InvalidDataException(label); checks.Add(label); };
            try
            {
                check(Devices.VersionText(0x0001000200030004UL) == "1.2.3.4", "Driver version uses four unsigned 16-bit fields");
                Dictionary<string, object> sizes = (Dictionary<string, object>)Devices.Sizes();
                check((int)sizes["device"] == 32 && (int)sizes["install"] == 584 && (int)sizes["driver"] == 1568 && (int)sizes["rank"] == 32, "Native x64 layouts match Microsoft SDK");
                List<Device> rows = Devices.Scan(); check(rows.Count > 0, "Actual device inventory returned");
                check(rows.All(d => !String.IsNullOrWhiteSpace(d.Id)) && rows.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == rows.Count, "Unique nonempty PnP identifiers");
                check(rows.Any(d => d.HardwareIds.Length > 0), "Actual hardware identifiers returned");
                check(rows.Any(d => !String.IsNullOrEmpty(d.Version)), "Installed driver versions read");
                Device device = rows.FirstOrDefault(d => d.HardwareIds.Length > 0 && !String.IsNullOrEmpty(d.Inf) && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", d.Inf)));
                check(device != null, "Installed INF located for real compatibility check");
                List<DriverCandidate> compatible = Devices.Match(device.Id, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", device.Inf));
                check(compatible.Count > 0 && compatible.All(c => c.Inf.EndsWith(device.Inf, StringComparison.OrdinalIgnoreCase)), "Windows matches actual device to its installed INF");
                check(compatible.SequenceEqual(compatible.OrderBy(c => c.Rank)), "Native rank orders compatible models");
                Device unrelated = rows.FirstOrDefault(d => d.Class != device.Class && d.HardwareIds.Length > 0 && !String.IsNullOrEmpty(d.Inf) && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", d.Inf)));
                check(unrelated != null && Devices.Match(device.Id, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", unrelated.Inf)).Count == 0, "Unrelated installed INF produces no compatible model");
                bool rejected = false; try { Devices.Match(device.Id, Path.Combine(store.Data, "missing.exe")); } catch (ArgumentException) { rejected = true; }
                check(rejected, "Missing and non-INF package rejected");
                string report = Api.Report(new List<Device> { new Device { Name = "<script>bad</script>", HardwareIds = new string[0], Problem = 0 } });
                check(!report.Contains("<script>") && report.Contains("&lt;script&gt;"), "Report escapes device text");
                store.Write("self-test-results.json", Store.Map("ok", true, "checks", checks, "devices", rows.Count, "problems", rows.Count(d => d.Problem.HasValue && d.Problem != 0), "readErrors", rows.Count(d => !String.IsNullOrEmpty(d.ReadError)), "compatibleModels", compatible.Count, "driverInstallations", 0, "layouts", sizes)); return 0;
            }
            catch (Exception ex) { store.Write("self-test-results.json", Store.Map("ok", false, "checks", checks, "error", ex.ToString())); return 1; }
        }
    }
}
