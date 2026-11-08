using System;
using System.Collections.Generic;
using System.IO;

namespace PCGuard
{
    // Изолированные проверки данных и границ API не обращаются к реальным защитным настройкам.
    internal static class SelfTests
    {
        internal static int Run(Store production)
        {
            var checks = new List<string>();
            var store = new Store(Path.Combine(production.Data, "Tests", "native-" + Guid.NewGuid().ToString("N")));
            Action<bool, string> check = delegate(bool condition, string label) { if (!condition) throw new Exception(label); checks.Add(label); };
            store.Write("sample.json", Store.Map("value", "Проверка UTF-8")); check(Store.Text(store.Read("sample.json"), "value") == "Проверка UTF-8", "Чтение и запись UTF-8");
            store.Write("sample.json", Store.Map("value", 43)); check(Convert.ToInt32(Store.Get(store.Read("sample.json"), "value")) == 43, "Замена JSON");
            string busyPath=Path.Combine(store.Data,"busy.json");Store.WritePath(busyPath,Store.Map("value",7));
            var held=new FileStream(busyPath,FileMode.Open,FileAccess.Read,FileShare.None);
            var release=new System.Threading.Thread(delegate(){System.Threading.Thread.Sleep(100);held.Dispose();});release.Start();
            check(Store.ReadText(busyPath).Contains("7"),"Краткая блокировка JSON не прерывает чтение статуса");release.Join();
            bool lockedRejected=false;using(var locked=new FileStream(busyPath,FileMode.Open,FileAccess.Read,FileShare.None)){try{Store.ReadText(busyPath);}catch(IOException){lockedRejected=true;}}
            check(lockedRejected,"Постоянно занятый JSON остаётся явной ошибкой");
            var noDelete=new FileStream(busyPath,FileMode.Open,FileAccess.Read,FileShare.Read);
            var releaseDelete=new System.Threading.Thread(delegate(){System.Threading.Thread.Sleep(100);noDelete.Dispose();});releaseDelete.Start();
            Store.WritePath(busyPath,Store.Map("value",8));releaseDelete.Join();
            check(Convert.ToInt32(Store.Get(Store.Object(Store.ReadPath(busyPath,null)),"value"))==8,"Атомарная запись переживает краткий запрет переименования");
            bool rejected = false; try { store.Write("../outside.json", Store.Map()); } catch (ArgumentException) { rejected = true; } check(rejected, "Отклонён обход пути");
            string id = new string('a', 32); store.Journal("Тест", "Выполнено", "Изолированная запись", id); store.Journal("Тест", "Выполнено", "Изолированная запись", id); check(store.History().Count == 1, "Нет дублей журнала при опросе");
            var api = new Api(store, delegate { });
            foreach (var input in new[] { Store.Map("action", "unknown"), Store.Map("action", "settings", "theme", "bad"), Store.Map("action", "scan", "type", "QuickScan"), Store.Map("action", "file", "path", @"\\server\share"), Store.Map("action", "startup_disable", "id", "../bad", "confirmed", true) })
            {
                rejected = false; try { api.Dispatch(input); } catch (ArgumentException) { rejected = true; } check(rejected, "Отклонён неверный запрос " + Store.Text(input, "action"));
            }
            var network = Store.Map("date", "test", "data", Store.Map("dns", new object[] { "127.0.0.1" })); store.Write("cache-network.json", network); api.Dispatch(Store.Map("action", "snapshot"));
            check(Store.Array(Store.Get(Store.Object(api.Dispatch(Store.Map("action", "compare"))), "changes")).Count == 0, "Одинаковые снимки совпадают");
            store.Write("cache-network.json", Store.Map("date", "test", "data", Store.Map("dns", new object[] { "127.0.0.2" })));
            check(Store.Array(Store.Get(Store.Object(api.Dispatch(Store.Map("action", "compare"))), "changes")).Count == 1, "Изменение DNS обнаружено");
            Store.WritePath(Path.Combine(production.Data, "Tests", "native-results.json"), Store.Map("checks", checks.Count, "results", checks)); return 0;
        }
    }
}
