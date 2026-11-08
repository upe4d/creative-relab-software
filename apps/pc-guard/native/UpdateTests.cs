using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
namespace PCGuard
{
    // Искусственный HTTP-транспорт проверяет откат без настоящей сети или образцов malware.
    internal static class UpdateTests
    {
        internal static void Run(string root,Action<bool,string> check,Action<Action,string> fails)
        {
            var store=new Store(Path.Combine(root,"Updater"));var engine=new Engine(store);string first=new string('a',40),second=new string('b',40);int calls=0;
            string path=Path.Combine(root,"updater-fixture.dat");File.WriteAllText(path,"Harmless updater fixture",Store.Utf8);string fileHash=SafeFiles.HashFile(path);
            byte[] body=Store.Utf8.GetBytes(fileHash+" fixture.dat\n");string blobHash;
            using(var sha=SHA1.Create())using(var stream=new MemoryStream()){byte[] prefix=System.Text.Encoding.ASCII.GetBytes("blob "+body.Length+"\0");stream.Write(prefix,0,prefix.Length);stream.Write(body,0,body.Length);stream.Position=0;blobHash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
            string commit=first;bool truncated=false,badBlob=false;
            Func<string,CancellationToken,byte[]> transport=delegate(string url,CancellationToken token){calls++;token.ThrowIfCancellationRequested();if(url.EndsWith("/commits/main",StringComparison.Ordinal))return Store.Utf8.GetBytes(Store.Json(Store.Map("sha",commit)));if(url.Contains("/git/trees/"))return Store.Utf8.GetBytes(Store.Json(Store.Map("truncated",truncated,"tree",new object[]{Store.Map("path","2026-11-06-updater-fixture.txt","type","blob","size",body.Length,"sha",badBlob?new string('0',40):blobHash)})));if(url.StartsWith("https://raw.githubusercontent.com/",StringComparison.Ordinal))return body;throw new IOException("Unexpected test URL");};
            var updater=new RuleUpdater(store,engine,transport);updater.Update(CancellationToken.None);
            check(engine.ManagedFeedCommit()==first,"Обновлятор фиксирует commit источника");
            check(Store.Text(engine.Scan(path),"verdict")=="published-ioc","Новый сетевой IOC остаётся предупреждением");
            check(engine.BlockHashes().Length==19216,"Обновление IOC не меняет блокирующие исторические правила");
            int before=calls;updater.Update(CancellationToken.None);check(calls==before,"Повторный клик не создаёт запросы чаще минуты");
            commit=second;store.Write("rule-update-state.json",Store.Map("checked",DateTimeOffset.Now.AddMinutes(-2).ToString("o"),"commit",first));
            string old=Store.Text(Store.Object(engine.Info()),"sha256");truncated=true;
            fails(delegate{updater.Update(CancellationToken.None);},"Неполный каталог не применяется");
            check(Store.Text(Store.Object(engine.Info()),"sha256")==old,"После отказа каталог и база сохранены");
            truncated=false;badBlob=true;fails(delegate{updater.Update(CancellationToken.None);},"Изменённый текст IOC отклоняется по объекту Git");
            check(Store.Text(Store.Object(engine.Info()),"sha256")==old,"Ошибка загрузки не заменяет действующую базу");
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();before=calls;bool stopped=false;try{updater.Update(cancel.Token);}catch(OperationCanceledException){stopped=true;}check(stopped&&before==calls,"Отмена до старта не обращается к сети");}
        }
    }
}
