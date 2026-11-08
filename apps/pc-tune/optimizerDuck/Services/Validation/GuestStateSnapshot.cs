using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace optimizerDuck.Services.Validation;

/// <summary>Читает фактическое состояние Windows отдельно от журнала выполнения.</summary>
internal static class GuestStateSnapshot
{
    internal static SortedDictionary<string, string> Capture(JObject scope, string item)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in scope["registry"]!.Values<string>())
            ReadRegistry(values, path!, 0);
        foreach (var name in scope["services"]!.Values<string>())
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name
            );
            values["service:" + name] =
                key == null
                    ? "absent"
                    : JsonConvert.SerializeObject(
                        new
                        {
                            Start = key.GetValue("Start"),
                            DelayedAutoStart = key.GetValue("DelayedAutoStart"),
                        }
                    );
        }
        foreach (var path in scope["tasks"]!.Values<string>())
        {
            try
            {
                using var task = TaskService.Instance.GetTask(path!);
                values["task:" + path] = task == null ? "absent" : "enabled=" + task.Enabled;
            }
            catch (Exception ex)
            {
                values["task:" + path] = "ERROR:" + ex.GetType().Name;
            }
        }
        if (item.Contains("PowerPlan", StringComparison.Ordinal))
        {
            values["power:active"] = Command("powercfg.exe", "/getactivescheme");
            values["power:list"] = Command("powercfg.exe", "/list");
        }
        if (item == "DisableHibernateAndFastStartup")
            values["hibernation:file-present"] = File.Exists(@"C:\hiberfil.sys").ToString();
        if (item == "DisableUSBPowerSaving")
        {
            try
            {
                using var search = new ManagementObjectSearcher(
                    @"root\wmi",
                    "SELECT InstanceName,Enable FROM MSPower_DeviceEnable"
                );
                using var rows = search.Get();
                foreach (ManagementObject row in rows)
                {
                    using (row)
                        values["usb:" + row["InstanceName"]] =
                            Convert.ToString(
                                row["Enable"],
                                global::System.Globalization.CultureInfo.InvariantCulture
                            ) ?? "null";
                }
            }
            catch (ManagementException ex)
            {
                values["usb:availability"] = ex.ErrorCode.ToString();
            }
        }
        return values;
    }

    internal static SortedDictionary<string, string> Protection()
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var name in new[]
            {
                "WinDefend",
                "WdNisSvc",
                "WdFilter",
                "WdBoot",
                "WdNisDrv",
                "SecurityHealthService",
                "wscsvc",
            }
        )
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name
            );
            result[name] =
                key == null
                    ? "absent"
                    : JsonConvert.SerializeObject(
                        new
                        {
                            Start = key.GetValue("Start"),
                            Delayed = key.GetValue("DelayedAutoStart"),
                            Image = key.GetValue("ImagePath"),
                        }
                    );
        }
        ReadRegistry(result, @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender", 0);
        return result;
    }

    private static void ReadRegistry(
        SortedDictionary<string, string> target,
        string path,
        int depth
    )
    {
        if (depth > 8 || target.Count > 20000)
            throw new InvalidOperationException("Превышен предел снимка реестра: " + path);
        var parts = path.Split('\\', 2);
        using var hive = RegistryKey.OpenBaseKey(
            parts[0].TrimEnd(':').Equals("HKCU", StringComparison.OrdinalIgnoreCase)
                ? RegistryHive.CurrentUser
                : RegistryHive.LocalMachine,
            RegistryView.Registry64
        );
        try
        {
            using var key = hive.OpenSubKey(parts[1]);
            target[path + "|key"] = key == null ? "absent" : "present";
            if (key == null)
                return;
            foreach (var name in key.GetValueNames())
                target[path + "|" + name] =
                    key.GetValueKind(name)
                    + ":"
                    + JsonConvert.SerializeObject(
                        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    );
            // Снимок ограничен точными ключами из исходников, без посторонних ветвей питания.
        }
        catch (Exception ex)
            when (ex is UnauthorizedAccessException or global::System.Security.SecurityException)
        {
            target[path + "|error"] = "ERROR:" + ex.GetType().Name;
        }
    }

    internal static string Command(string executable, string arguments)
    {
        using var process = Process.Start(
            new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        )!;
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30000) || process.ExitCode != 0)
            throw new InvalidOperationException(executable + " query failed");
        return output.Trim();
    }

    internal static object[] Difference(
        SortedDictionary<string, string> before,
        SortedDictionary<string, string> after
    ) =>
        before
            .Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(key => before.GetValueOrDefault(key) != after.GetValueOrDefault(key))
            .Select(key =>
                (object)
                    new
                    {
                        key,
                        before = before.GetValueOrDefault(key),
                        after = after.GetValueOrDefault(key),
                    }
            )
            .ToArray();
}
