using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCGuard
{
    // Системный модуль запускается отдельным скрытым процессом; аргументы находятся в JSON.
    internal sealed class Jobs
    {
        private readonly Store store;
        private readonly object gate = new object();
        private readonly string[] actions = { "status", "startup", "network", "threats", "file", "pick", "scan", "update", "startup_disable", "startup_restore", "open", "camera", "camera_disable", "camera_restore", "camera_apps", "camera_app_deny", "camera_app_restore" };
        private readonly string[] mutations = { "scan", "update", "startup_disable", "startup_restore", "camera_disable", "camera_restore", "camera_app_deny", "camera_app_restore" };
        internal Jobs(Store store) { this.store = store; }
        internal string Start(string action, Dictionary<string, object> arguments)
        {
            if (!actions.Contains(action)) throw new ArgumentException("Неизвестная системная операция.");
            lock (gate)
            {
                if (mutations.Contains(action))
                {
                    foreach (string file in Directory.GetFiles(Path.Combine(store.Data, "Jobs"), "*.result.json"))
                    {
                        var previous = Store.Object(Store.ReadPath(file, null));
                        if (mutations.Contains(Store.Text(previous, "action")) && new[] { "queued", "running" }.Contains(Store.Text(previous, "state")) && Now() - Convert.ToInt64(Store.Get(previous, "created", 0)) < 86400)
                            throw new InvalidOperationException("Дождись завершения текущей операции изменения или проверки.");
                    }
                }
                string id = Guid.NewGuid().ToString("N"), prefix = Path.Combine(store.Data, "Jobs", id);
                Store.WritePath(prefix + ".request.json", Store.Map("id", id, "action", action, "arguments", arguments, "created", Now()));
                var result = Store.Map("id", id, "action", action, "created", Now(), "state", "queued", "message", "Подготовка операции");
                Store.WritePath(prefix + ".result.json", result);
                try
                {
                    string worker = Path.Combine(store.Root, "system", "worker.ps1");
                    var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile -STA -ExecutionPolicy Bypass -File \"" + worker + "\" -JobId " + id)
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = store.Root };
                    using (Process process = Process.Start(info)) if (process == null) throw new IOException("Процесс не создан.");
                }
                catch (Exception error)
                {
                    result["state"] = "error"; result["message"] = "Не удалось запустить модуль Windows: " + error.Message;
                    Store.WritePath(prefix + ".result.json", result);
                }
                return id;
            }
        }
        internal Dictionary<string, object> Get(string id)
        {
            if (!Regex.IsMatch(id, @"\A[a-f0-9]{32}\z")) throw new ArgumentException("Некорректный идентификатор задания.");
            string path = Path.Combine(store.Data, "Jobs", id + ".result.json");
            var result = ReadResult(path, id);
            string state = Store.Text(result, "state"); long age = Now() - Convert.ToInt64(Store.Get(result, "created", Now()));
            if ((state == "queued" && age > 60) || (state == "running" && age > 86400))
            {
                result["state"] = state = "error"; result["message"] = "Системный модуль не ответил. Результат операции неизвестен; проверь журнал задания.";
                Store.WritePath(path, result);
            }
            if (state == "done" || state == "error")
            {
                lock (Store.Gate)
                {
                    if (!File.Exists(Path.Combine(store.Data, "Jobs", id + ".logged")))
                    {
                        string action = Store.Text(result, "action");
                        if (state == "done" && new[] { "status", "startup", "network", "threats", "file", "camera" }.Contains(action))
                            store.Write("cache-" + action + ".json", Store.Map("date", DateTimeOffset.Now.ToString("o"), "data", Store.Get(result, "data")));
                        store.Journal(Label(action), state == "done" ? "Выполнено" : "Ошибка", Store.Text(result, "message"), id);
                    }
                }
            }
            return result;
        }
        // File.Exists скрывает ошибки совместного доступа; читаем результат напрямую.
        // Короткий промежуток замены известного задания не означает его исчезновение.
        private static Dictionary<string, object> ReadResult(string path, string expectedId)
        {
            string request = path.Substring(0, path.Length - ".result.json".Length) + ".request.json";
            for (int attempt = 0; ; attempt++)
            {
                try {
                    var value = Store.Object(Store.Serializer().DeserializeObject(Store.ReadText(path)));
                    if (Store.Text(value, "id") != expectedId || !new[] { "queued", "running", "done", "error" }.Contains(Store.Text(value, "state")))
                        throw new InvalidDataException("Повреждён результат задания. Операция повторно не запускается.");
                    return value;
                } catch (FileNotFoundException) {
                    if (attempt >= 5 || !File.Exists(request)) throw new IOException("Задание не найдено.");
                    System.Threading.Thread.Sleep(10 << attempt);
                }
            }
        }
        private static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }
        private static string Label(string action)
        {
            var labels = new Dictionary<string, string> { {"status","Состояние защиты"},{"startup","Чтение точек запуска"},{"network","Аудит сети и браузеров"},{"threats","История угроз"},{"file","Анализ файла"},{"pick","Выбор пути"},{"scan","Антивирусная проверка"},{"update","Обновление баз Defender"},{"startup_disable","Отключение автозапуска"},{"startup_restore","Возврат автозапуска"},{"open","Открытие системного окна"} };
            if(action=="camera")return "Сведения о камере";if(action=="camera_disable")return "Отключение камеры";if(action=="camera_restore")return "Возврат камеры";
            if(action=="camera_apps")return "Разрешения камеры для пакетных приложений";if(action=="camera_app_deny")return "Запрет камеры пакетному приложению";if(action=="camera_app_restore")return "Возврат политики камеры приложения";
            return labels.ContainsKey(action) ? labels[action] : action;
        }
    }
}
