using System;
using System.IO;
using Microsoft.Win32;

namespace PCGuard
{
    internal sealed class Autostart
    {
        private readonly Store store;private const string Key=@"Software\Microsoft\Windows\CurrentVersion\Run",Name="PC Guard";
        internal Autostart(Store store){this.store=store;}
        private string Command{get{return "\""+Path.Combine(store.Root,"PC Guard.exe")+"\" --agent";}}
        internal object Status()
        {
            try{using(var key=Registry.CurrentUser.OpenSubKey(Key,false)){object value=key==null?null:key.GetValue(Name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);return Store.Map("configured",Object.Equals(value,Command),"conflict",value!=null&&!Object.Equals(value,Command),"note","Это запись Run. Политики Windows или запрет автозапуска могут дополнительно влиять на запуск.");}}
            catch(Exception error){return Store.Map("configured",false,"unknown",true,"note",error.Message);}
        }
        internal object Set(bool enabled)
        {
            using(var key=Registry.CurrentUser.CreateSubKey(Key))
            {
                object value=key.GetValue(Name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                if(value!=null&&!Object.Equals(value,Command))throw new IOException("С таким именем уже есть другая запись. Она не изменена.");
                if(enabled)key.SetValue(Name,Command,RegistryValueKind.String);else if(value!=null)key.DeleteValue(Name,true);
                object after=key.GetValue(Name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                if(enabled&&!Object.Equals(after,Command)||!enabled&&after!=null)throw new IOException("Изменение автозапуска не подтверждено.");
            }
            store.Journal("Автозапуск PC Guard","Выполнено",enabled?"Добавлена собственная запись для запуска в фоне.":"Собственная запись автозапуска удалена.");return Status();
        }
    }
}
