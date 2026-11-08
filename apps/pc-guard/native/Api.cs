using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCGuard
{
    // Разрешены только заранее определённые операции; произвольных команд API не принимает.
    internal sealed class Api
    {
        private readonly Store store; private readonly Jobs jobs; private readonly Action shutdown; private readonly Suite suite;
        internal Api(Store store, Action shutdown,Suite suite=null) { this.store = store; jobs = new Jobs(store); this.shutdown = shutdown; this.suite=suite; }
        internal object Dispatch(Dictionary<string, object> input)
        {
            string action = Store.Text(input, "action");
            if(action.StartsWith("own_",StringComparison.Ordinal)||action.StartsWith("suite_",StringComparison.Ordinal)||action.StartsWith("vault_",StringComparison.Ordinal)||action.StartsWith("folder_",StringComparison.Ordinal)){if(suite==null)throw new InvalidOperationException("Модуль не загружен.");return suite.Dispatch(action,input);}
            switch (action)
            {
                case "meta":
                    var settings = store.Read("settings.json"); if (!settings.ContainsKey("theme")) settings["theme"] = "dark";
                    return Store.Map("version", "3.4.2", "root", store.Root, "settings", settings, "history", store.History());
                case "settings":
                    string theme = Store.Text(input, "theme"); if (theme != "dark" && theme != "light") throw new ArgumentException("Неизвестная тема.");
                    store.Write("settings.json", Store.Map("theme", theme)); return Store.Map("theme", theme);
                case "history": return Store.Map("rows", store.History());
                case "job": return jobs.Get(Store.Text(input, "id"));
                case "shutdown": shutdown(); return Store.Map("closing", true);
                case "report": return Report();
                case "export":
                    string reportPath = Path.Combine(store.Data, "Reports", "PC-Guard-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".json");
                    Store.WritePath(reportPath, Report()); return Store.Map("path", reportPath);
                case "snapshot":
                    var network = store.Read("cache-network.json"); if (network.Count == 0) throw new InvalidOperationException("Сначала выполни аудит сети.");
                    store.Write("network-baseline.json", network); store.Journal("Снимок сети", "Выполнено", "Сохранена конфигурация для сравнения."); return network;
                case "compare":
                    var baseline = store.Read("network-baseline.json"); var current = store.Read("cache-network.json");
                    if (baseline.Count == 0 || current.Count == 0) throw new InvalidOperationException("Нужны сохранённый снимок и новый аудит сети.");
                    var changes = new List<object>(); var before = Store.Object(Store.Get(baseline, "data")); var after = Store.Object(Store.Get(current, "data"));
                    foreach (string key in new[] { "dns", "proxy", "hosts", "extensions" })
                        if (Store.Json(Store.Get(before, key)) != Store.Json(Store.Get(after, key))) changes.Add(Store.Map("section", key, "before", Store.Get(before, key), "after", Store.Get(after, key)));
                    return Store.Map("baseline", Store.Get(baseline, "date"), "current", Store.Get(current, "date"), "changes", changes);
                case "status": case "startup": case "network": case "threats": case "camera": return Start(action, Store.Map());
                case "camera_apps":return Start(action,Store.Map());
                case "camera_app_deny":case "camera_app_restore":Confirm(input);return Start(action,Store.Map("family",Store.Text(input,"family")));
                case "camera_disable":case "camera_restore":Confirm(input);return Start(action,Store.Map("id",Store.Text(input,"id")));
                case "pick":
                    string kind = Store.Text(input, "kind"); if (kind != "file" && kind != "folder") throw new ArgumentException("Неизвестный способ выбора.");
                    return Start(action, Store.Map("kind", kind));
                case "file": return Start(action, Store.Map("path", LocalPath(input)));
                case "scan":
                    Confirm(input); string type = Store.Text(input, "type"); if (!new[] { "QuickScan", "FullScan", "CustomScan" }.Contains(type)) throw new ArgumentException("Неизвестный режим проверки.");
                    return Start(action, Store.Map("type", type, "path", type == "CustomScan" ? LocalPath(input) : null));
                case "update": Confirm(input); return Start(action, Store.Map());
                case "startup_disable": case "startup_restore":
                    Confirm(input); string id = Store.Text(input, "id"); if (!Regex.IsMatch(id, @"\A[a-f0-9]{64}\z")) throw new ArgumentException("Не выбрана запись автозапуска.");
                    var cache = Store.Object(Store.Get(store.Read("cache-startup.json"), "data"));
                    var matches = Store.Array(Store.Get(cache, "rows")).Select(Store.Object).Where(r => Store.Text(r, "id") == id).ToList();
                    if (matches.Count != 1) throw new InvalidOperationException("Запись отсутствует в последнем списке. Обнови таблицу.");
                    if (!new[] { "registry", "folder", "backup" }.Contains(Store.Text(matches[0], "kind"))) throw new InvalidOperationException("Службы и задания доступны для просмотра.");
                    return Start(action, Store.Map("entry", matches[0]));
                case "open":
                    string target = Store.Text(input, "target"); if (!new[] { "security", "history", "services", "tasks", "data", "explorer", "camera" }.Contains(target)) throw new ArgumentException("Недопустимое системное окно.");
                    var arguments = Store.Map("target", target); if (target == "explorer") arguments["path"] = LocalPath(input);
                    return Start(action, arguments);
                default: throw new ArgumentException("Неизвестный запрос.");
            }
        }
        private object Start(string action, Dictionary<string, object> arguments) { return Store.Map("job", jobs.Start(action, arguments)); }
        private static void Confirm(Dictionary<string, object> input) { if (!Object.Equals(Store.Get(input, "confirmed"), true)) throw new ArgumentException("Действие требует подтверждения в интерфейсе."); }
        private static string LocalPath(Dictionary<string, object> input)
        {
            string path = Store.Text(input, "path"); if (path.Length > 2000 || !Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]") || Regex.IsMatch(path, @"[\x00-\x1F]")) throw new ArgumentException("Нужен полный путь к файлу или папке на локальном диске."); return path;
        }
        private object Report()
        {
            var result = Store.Map("application", "PC Guard", "version", "3.4.2", "created", DateTimeOffset.Now.ToString("o"), "journal", store.History());
            if(suite!=null)result["suite"]=suite.Status();
            foreach (string section in new[] { "status", "startup", "network", "threats", "file" }) result[section] = store.Read("cache-" + section + ".json"); return result;
        }
    }
}
