using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PCGuard
{
    // Только искусственные файлы и собственный профиль данных. Камера и антивирусы не переключаются.
    internal static class SuiteTests
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        private static void WriteStreamFixture(string path){using(var handle=CreateFile(path,0x40000000,3,IntPtr.Zero,2,0x80,IntPtr.Zero)){if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());using(var file=new FileStream(handle,FileAccess.Write)){file.WriteByte(5);file.WriteByte(6);}}}
        internal static int Run(Store production)
        {
            string root=Path.Combine(production.Data,"Tests","suite-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            string files=Path.Combine(root,"Files"),output=Path.Combine(root,"Output"),other=Path.Combine(root,"Other");Directory.CreateDirectory(files);Directory.CreateDirectory(output);Directory.CreateDirectory(other);
            var store=new Store(Path.Combine(root,"App"));var checks=new List<string>();var limitations=new List<object>();
            Action<bool,string> check=delegate(bool value,string name){if(!value)throw new InvalidOperationException("Проверка не пройдена: "+name);checks.Add(name);Store.WritePath(Path.Combine(production.Data,"Tests","suite-progress.json"),Store.Map("checks",checks.Count,"results",checks));};
            Action<Action,string> fails=delegate(Action action,string name){bool failed=false;try{action();}catch(Exception error){if(error is IOException||error is InvalidOperationException||error is CryptographicException||error is ArgumentException||error is System.ComponentModel.Win32Exception)failed=true;else throw;}check(failed,name);};
            using(var suite=new Suite(store,false))
            {
                Action<int,string,string> refuseEditor=delegate(int index,string fragment,string name){try{suite.Driver.LaunchEditor(Store.Map("index",index));check(false,name);}catch(IOException error){check(error.Message.IndexOf(fragment,StringComparison.OrdinalIgnoreCase)>=0,name);}};
                refuseEditor(-1,"Выберите","Защищённый запуск отвергает индекс вне сохранённого списка");
                string fakeEditor=Path.Combine(files,"editor-negative.exe");File.WriteAllText(fakeEditor,"Synthetic negative fixture");
                store.Write("driver-config.json",Store.Map("allowedApps",new object[]{fakeEditor}));
                refuseEditor(0,"сохранённого SHA","Редактор без сохранённого SHA не запускается");
                store.Write("driver-config.json",Store.Map("allowedApps",new object[]{fakeEditor},"allowedAppHashes",Store.Map(fakeEditor,new string('0',64))));
                refuseEditor(0,"изменился","Изменённый редактор отвергается до обращения к драйверу и токенам");
                store.Write("driver-config.json",Store.Map());
                var baseline=Store.Object(suite.Engine.Info());
                check(Convert.ToInt32(Store.Get(baseline,"hashes"))==19788,"Встроены 19788 уникальных опубликованных SHA-256");
                check(suite.Engine.BlockHashes().Length==19216,"572 индикатора кампаний исключены из блокирующей политики");
                string clean=Path.Combine(files,"clean.dat"),marker=Path.Combine(files,"marker.dat");File.WriteAllText(clean,"Ordinary fixture with no rules.",Store.Utf8);File.WriteAllText(marker,Engine.TestMarker,Store.Utf8);
                check(Store.Text(suite.Engine.Scan(clean),"verdict")=="no-match","Обычный файл не объявлен угрозой");
                check(Store.Text(suite.Engine.Scan(marker),"verdict")=="test-detection","Безопасная метка обнаруживается отдельно от malware");
                using(var locked=new FileStream(clean,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(Store.Text(suite.Engine.Scan(clean),"verdict")=="not-scanned","Занятый файл не объявлен чистым");
                check(!SafeFiles.Within(files+"-other",files),"Границы путей учитывают разделитель");
                string fixtureHash=SafeFiles.HashFile(clean),db=Path.Combine(root,"rules.json");
                Store.WritePath(db,Store.Map("schema",1,"version","isolated-fixture","hashes",new object[]{Store.Map("sha256",fixtureHash,"name","Synthetic rule, not real malware","source","isolated test")}));
                fails(delegate{suite.Engine.Import(db,new string('0',64));},"Импорт с неверным SHA-256 отклонён");
                suite.Engine.Import(db,SafeFiles.HashFile(db));var known=suite.Engine.Scan(clean);
                check(Store.Text(known,"verdict")=="known-threat","Точное совпадение с изолированным правилом обнаружено");
                string alertDb=Path.Combine(root,"alerts.json");
                Store.WritePath(alertDb,Store.Map("schema",1,"version","alert-fixture","hashes",new object[]{Store.Map("sha256",fixtureHash,"name","Synthetic campaign indicator","action","alert")}));
                suite.Engine.Import(alertDb,SafeFiles.HashFile(alertDb));var alert=suite.Engine.Scan(clean);
                check(Store.Text(alert,"verdict")=="published-ioc","Индикатор кампании не выдаётся за подтверждённое malware");
                check(suite.Engine.BlockHashes().Length==0,"Предупреждающий IOC не выгружается в драйвер как блокирующий");
                fails(delegate{suite.Storage.Quarantine(alert);},"Индикатор кампании не разрешает карантин");
                suite.Engine.Import(db,SafeFiles.HashFile(db));
                File.AppendAllText(clean,"Changed",Store.Utf8);fails(delegate{suite.Storage.Quarantine(known);},"Изменившийся после проверки файл не удаляется");check(File.Exists(clean),"Оригинал после отказа карантина сохранён");
                File.WriteAllText(clean,"Ordinary fixture with no rules.",Store.Utf8);known=suite.Engine.Scan(clean);
                var quarantined=Store.Object(suite.Storage.Quarantine(known));check(!File.Exists(clean),"Карантин удаляет проверенный оригинал по дескриптору");
                check(Store.Text(quarantined,"state")=="Quarantined","Успех карантина подтверждён");
                var restored=Store.Object(suite.Storage.RestoreQuarantine(Store.Text(quarantined,"id"),output));check(SafeFiles.HashFile(Store.Text(restored,"path"))==fixtureHash,"Возврат карантина сохраняет байты");
                fails(delegate{suite.Storage.RestoreQuarantine(Store.Text(quarantined,"id"),output);},"Возврат не перезаписывает существующий файл");
                string password=Guid.NewGuid().ToString("N");string plaintext=Path.Combine(files,"private.dat");File.WriteAllText(plaintext,"Confidential fixture: "+Guid.NewGuid(),Store.Utf8);string originalHash=SafeFiles.HashFile(plaintext);
                var encrypted=Store.Object(suite.Vault.Encrypt(plaintext,password));string vaultId=Store.Text(encrypted,"id");check(File.Exists(plaintext),"Сейф сохраняет исходный файл");
                fails(delegate{suite.Vault.Decrypt(vaultId,other,Guid.NewGuid().ToString("N"));},"Неверный пароль не расшифровывает данные");check(!Directory.GetFiles(other).Any(),"При неверном пароле открытый файл не создаётся");
                var decrypted=Store.Object(suite.Vault.Decrypt(vaultId,other,password));check(SafeFiles.HashFile(Store.Text(decrypted,"path"))==originalHash,"Сейф восстанавливает точные байты");
                fails(delegate{suite.Vault.Decrypt(vaultId,other,password);},"Сейф отказывает при конфликте назначения");
                string container=Store.Text(encrypted,"path");byte[] damaged=File.ReadAllBytes(container);damaged[80]^=1;File.WriteAllBytes(container,damaged);
                fails(delegate{suite.Vault.Decrypt(vaultId,output,password);},"Изменение шифротекста обнаруживается по HMAC");
                string watch=Path.Combine(root,"Watch");Directory.CreateDirectory(watch);suite.Realtime.Configure(new[]{watch},true,false);
                string eventFile=Path.Combine(watch,"created.dat");File.WriteAllText(eventFile,Engine.TestMarker,Store.Utf8);
                DateTime deadline=DateTime.UtcNow.AddSeconds(15);bool found=false;
                while(DateTime.UtcNow<deadline){var rows=Store.Array(Store.Get(store.Read("engine-findings.json"),"rows"));if(rows.Select(Store.Object).Any(r=>Store.Text(r,"path")==eventFile&&Store.Text(r,"verdict")=="test-detection")){found=true;break;}Thread.Sleep(200);}
                check(found,"Новый файл проверяется фоновым наблюдателем");suite.Realtime.Configure(new[]{watch},true,true);
                string automatic=Path.Combine(watch,"automatic.dat");File.WriteAllText(automatic,"Ordinary fixture with no rules.",Store.Utf8);deadline=DateTime.UtcNow.AddSeconds(15);bool isolatedAutomatically=false;
                while(DateTime.UtcNow<deadline){if(!File.Exists(automatic)&&Store.Array(suite.Storage.QuarantineRows()).Select(Store.Object).Any(r=>Store.Text(r,"path")==automatic&&Store.Text(r,"state")=="Quarantined")){isolatedAutomatically=true;break;}Thread.Sleep(200);}
                check(isolatedAutomatically,"Точная изолированная сигнатура автоматически помещается в карантин");suite.Realtime.Configure(new[]{watch},false,false);
                check(!Object.Equals(Store.Get(Store.Object(suite.Realtime.Status()),"enabled"),true),"Фоновое наблюдение выключается");
                string documents=Path.Combine(root,"Documents");Directory.CreateDirectory(documents);
                string textProbe=Path.Combine(root,"access-probe.txt");File.WriteAllText(textProbe,"PC Guard access probe",Store.Utf8);var probe=suite.Engine.Scan(textProbe);if(Store.Text(probe,"verdict")=="not-scanned")limitations.Add(Store.Map("feature","TXT read access","message",Store.Get(probe,"note")));
                string document=Path.Combine(documents,"document.dat");File.WriteAllText(document,"Original document fixture",Store.Utf8);string docHash=SafeFiles.HashFile(document);
                var protection=Store.Object(suite.Folders.Protect(documents,true));check(Convert.ToInt32(Store.Get(protection,"lockedFiles"))==1,"Документ защищён удерживаемым дескриптором");
                fails(delegate{File.WriteAllText(document,"Overwritten");},"Запись в защищённый документ блокируется Windows");
                fails(delegate{File.Delete(document);},"Удаление защищённого документа блокируется Windows");
                var manifest=Store.Object(Store.Get(protection,"manifest"));var fileRow=Store.Object(Store.Array(Store.Get(manifest,"files"))[0]);
                suite.Folders.Unlock();File.WriteAllText(document,"User edit after unlock",Store.Utf8);check(File.ReadAllText(document).Contains("User edit"),"После снятия блокировки редактирование доступно");
                string recovered=Path.Combine(root,"Recovered");Directory.CreateDirectory(recovered);var recovery=Store.Object(suite.Folders.Restore(Store.Text(fileRow,"backup"),recovered));
                check(SafeFiles.HashFile(Store.Text(recovery,"path"))==docHash,"Зашифрованный резерв возвращает исходный документ");
                var state=Store.Object(suite.Status());check(!Object.Equals(Store.Get(Store.Object(Store.Get(state,"driver")),"preExecutionBlocking"),true),"Недоступный системный перехват не выдаётся за активный");
                fails(delegate{suite.Driver.Configure(Store.Map("execRoots",new[]{files}),suite.Engine);},"Незагруженный драйвер не принимает политику как активную");
            }
            string catalogRoot=Path.Combine(root,"Catalog");Directory.CreateDirectory(catalogRoot);string catalogFile=Path.Combine(catalogRoot,"fixture.dat");File.WriteAllBytes(catalogFile,new byte[]{1,2,3});
            using(var catalog=new ProtectedCatalog(new[]{catalogRoot})){check(catalog.Handles().Length==1,"Каталог собирает реальный дескриптор тестового файла");fails(delegate{File.WriteAllBytes(catalogFile,new byte[]{4});},"Подготовка удерживает файл против параллельной записи");fails(delegate{Directory.Move(catalogRoot,catalogRoot+"-moved");},"Подготовка удерживает каталог против переименования");}
            File.WriteAllBytes(catalogFile,new byte[]{4});check(File.ReadAllBytes(catalogFile)[0]==4,"Отмена подготовки освобождает файловые дескрипторы");
            using(var busy=new FileStream(catalogFile,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite))fails(delegate{using(var ignored=new ProtectedCatalog(new[]{catalogRoot})){}},"Старый пишущий дескриптор не пропускается подготовкой");
            WriteStreamFixture(catalogFile+":fixture");using(var catalog=new ProtectedCatalog(new[]{catalogRoot}))check(catalog.Handles().Length==2,"Подготовка включает именованный NTFS-поток");
            string emptyRoot=Path.Combine(root,"EmptyCatalog");Directory.CreateDirectory(emptyRoot);using(var empty=new ProtectedCatalog(new[]{emptyRoot}))check(empty.Handles().Length==0,"Пустая папка корректно подготавливается");
            UpdateTests.Run(root,check,fails);
            Store.WritePath(Path.Combine(production.Data,"Tests","suite-results.json"),Store.Map("checks",checks.Count,"results",checks,"limitations",limitations,"fixture",root,"realMalwareExecuted",false,"cameraChanged",false,"defenderChanged",false,"iobitChanged",false));return 0;
        }
    }
}
