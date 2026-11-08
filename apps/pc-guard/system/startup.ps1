# Автозапуск: чтение Run/Startup, служб и заданий. Изменения ограничены Run и Startup.
function Get-GuardRegistryLocations {
    if ($script:GuardTestRegistry) { return @([pscustomobject]@{hive='HKCU';view=64;key=$script:GuardTestRegistry}) }
    return @([pscustomobject]@{hive='HKCU';view=64;key='Software\Microsoft\Windows\CurrentVersion\Run'},[pscustomobject]@{hive='HKLM';view=64;key='Software\Microsoft\Windows\CurrentVersion\Run'},[pscustomobject]@{hive='HKLM';view=32;key='Software\Microsoft\Windows\CurrentVersion\Run'})
}
function Get-GuardStartupFolders {
    if ($script:GuardTestStartupFolder) { return @($script:GuardTestStartupFolder) }
    return @([Environment]::GetFolderPath('Startup'),[Environment]::GetFolderPath('CommonStartup')) | Where-Object { $_ }
}
function Open-GuardRegistry([string]$Hive,[int]$View) {
    if($Hive -notin @('HKCU','HKLM') -or $View -notin @(32,64)){throw 'Неизвестное расположение реестра.'}
    $hiveValue=if($Hive-eq'HKCU'){[Microsoft.Win32.RegistryHive]::CurrentUser}else{[Microsoft.Win32.RegistryHive]::LocalMachine}
    $viewValue=if($View-eq32){[Microsoft.Win32.RegistryView]::Registry32}else{[Microsoft.Win32.RegistryView]::Registry64}
    return [Microsoft.Win32.RegistryKey]::OpenBaseKey($hiveValue,$viewValue)
}
function Get-GuardRegistryId($Entry) { return Get-GuardId ($Entry.hive+'|'+$Entry.view+'|'+$Entry.key+'|'+$Entry.name) }
function Get-GuardBackups { return @(Get-GuardJson (Join-Path $GuardData 'startup-backups.json') @()) }
function Save-GuardBackups($Records) { Write-GuardJson (Join-Path $GuardData 'startup-backups.json') @($Records) }
function Test-GuardProtected([string]$Name,[string]$Command) {
    return ($Name+' '+$Command)-match'(?i)IObit|Malware\s*Fighter|Windows\s*Defender|MsMpEng|SecurityHealth|PC Guard\.exe'
}

function Get-GuardApproval([string]$Hive,[string]$Group,[string]$Name) {
    if($script:GuardTestRegistry){return 'on'}
    $base=$null;$key=$null
    try{
        $base=Open-GuardRegistry $Hive 64;$key=$base.OpenSubKey(('Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\'+$Group),$false)
        $value=$null;if($key){$value=$key.GetValue($Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
        if($null-eq$value){return 'on'}
        if($value-isnot[byte[]]-or$value.Length-lt12){return 'unknown'}
        $state=[BitConverter]::ToUInt32($value,0);if($state-gt7){return 'unknown'}
        if(($state-band1)-ne0){return 'off'};return 'on'
    }catch{return 'unknown'}finally{if($key){$key.Dispose()};if($base){$base.Dispose()}}
}

function Add-GuardEntryMetadata($Entry,[Collections.Generic.Dictionary[string,object]]$Cache) {
    $path=Get-TargetPath ([string]$Entry.command)
    if($Entry.kind-eq'folder'){
        try{if([IO.Path]::GetExtension($Entry.originalPath)-ieq'.lnk'){$shell=New-Object -ComObject WScript.Shell;$shortcut=$shell.CreateShortcut($Entry.originalPath);$path=$shortcut.TargetPath;[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut);[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)}}catch{}
    }
    $brief=$null
    if($path){if($Cache.ContainsKey($path)){$brief=$Cache[$path]}else{try{$brief=Get-FileBrief $path}catch{$brief=[pscustomobject]@{size=$null;signature='Недоступна';publisher=''}};$Cache[$path]=$brief}}
    $Entry.path=$path;$Entry.size=$null;$Entry.signature='Файл не определён';$Entry.publisher=''
    if($brief){$Entry.size=$brief.size;$Entry.signature=$brief.signature;$Entry.publisher=$brief.publisher}
    $Entry.protected=Test-GuardProtected ([string]$Entry.name) ([string]$Entry.command)
    return [pscustomobject]$Entry
}

function Get-GuardStartup {
    $rows=New-Object 'Collections.Generic.List[object]';$issues=New-Object 'Collections.Generic.List[string]'
    $cache=New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($location in @(Get-GuardRegistryLocations)){
        $base=$null;$key=$null
        try{
            $base=Open-GuardRegistry $location.hive $location.view;$key=$base.OpenSubKey($location.key,$false);if(-not$key){continue}
            foreach($name in @($key.GetValueNames()|Select-Object -First 500)){
                $value=$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if($value-isnot[string]){continue}
                $group=if($location.hive-eq'HKLM'-and$location.view-eq32){'Run32'}else{'Run'}
                $approval=Get-GuardApproval $location.hive $group $name
                $entry=@{id='';name=$name;kind='registry';type='Run';hive=$location.hive;view=$location.view;key=$location.key;valueKind=[int]$key.GetValueKind($name);command=$value;state=$approval;status=$(if($approval-eq'on'){'Запуск разрешён'}elseif($approval-eq'off'){'Выключено Windows'}else{'Статус не определён'});source=($location.hive+' · '+$location.view+' bit');originalPath=$null}
                $entry.id=Get-GuardRegistryId ([pscustomobject]$entry);$rows.Add((Add-GuardEntryMetadata $entry $cache))
            }
        }catch{$issues.Add($location.hive+': '+$_.Exception.Message)}finally{if($key){$key.Dispose()};if($base){$base.Dispose()}}
    }
    foreach($folder in @(Get-GuardStartupFolders)){
        if(-not(Test-Path -LiteralPath $folder)){continue}
        try{
            foreach($file in @(Get-ChildItem -LiteralPath $folder -File -Force|Select-Object -First 500)){
                if($file.Name-ieq'desktop.ini'){continue}
                $hive=if($folder-ieq[Environment]::GetFolderPath('CommonStartup')){'HKLM'}else{'HKCU'};$approval=Get-GuardApproval $hive 'StartupFolder' $file.Name
                $entry=@{id=(Get-GuardId $file.FullName);name=$file.Name;kind='folder';type='Startup';command=$file.FullName;state=$approval;status=$(if($approval-eq'on'){'Запуск разрешён'}elseif($approval-eq'off'){'Выключено Windows'}else{'Статус не определён'});source=$folder;originalPath=$file.FullName}
                $rows.Add((Add-GuardEntryMetadata $entry $cache))
            }
        }catch{$issues.Add('Startup: '+$_.Exception.Message)}
    }
    if(-not$script:GuardTestRegistry){
        try{
            foreach($service in @(Get-CimInstance Win32_Service|Select-Object -First 1000)){
                $state=if($service.StartMode-eq'Auto'){'on'}elseif($service.StartMode-eq'Disabled'){'off'}else{'unknown'}
                $entry=@{id=(Get-GuardId ('service|'+$service.Name));name=$service.DisplayName;kind='service';type='Служба';command=$service.PathName;state=$state;status=$(if($state-eq'on'){'Автоматически'}elseif($state-eq'off'){'Выключено'}else{'Вручную'});source=($service.Name+' · '+$service.State);originalPath=$null}
                # Подпись для большого списка проверяется отдельно в анализе файла, без задержки всего экрана.
                $entry.path=Get-TargetPath $service.PathName;$entry.size=$null;$entry.signature='Проверить файл';$entry.publisher='';$entry.protected=$true
                if($entry.path){try{$entry.size=(Get-Item -LiteralPath $entry.path).Length}catch{}}
                $rows.Add([pscustomobject]$entry)
            }
        }catch{$issues.Add('Службы: '+$_.Exception.Message)}
        try{
            foreach($task in @(Get-ScheduledTask|Select-Object -First 1000)){
                $command=(@($task.Actions|ForEach-Object{($_.Execute+' '+$_.Arguments).Trim()})-join '; ');$state=if($task.Settings.Enabled){'on'}else{'off'}
                $path=$null;if(@($task.Actions).Count-eq1){$path=Get-TargetPath ([string]$task.Actions[0].Execute)}
                $entry=@{id=(Get-GuardId ('task|'+$task.TaskPath+$task.TaskName));name=$task.TaskName;kind='task';type='Задание';command=$command;state=$state;status=$(if($state-eq'on'){'Задание включено'}else{'Задание выключено'});source=$task.TaskPath;path=$path;size=$null;signature='Проверить файл';publisher='';protected=$true;originalPath=$null}
                if($path){try{$entry.size=(Get-Item -LiteralPath $path).Length}catch{}}
                $rows.Add([pscustomobject]$entry)
            }
        }catch{$issues.Add('Планировщик: '+$_.Exception.Message)}
    }
    try{foreach($backup in @(Get-GuardBackups|Where-Object{$_.state-ne'Restored'})){
        $existing=@($rows|Where-Object{$_.id-eq$backup.id})
        if($existing.Count){foreach($item in $existing){$item|Add-Member -NotePropertyName hasBackup -NotePropertyValue $true -Force};continue}
        $rows.Add([pscustomobject]@{id=$backup.id;name=$backup.name;kind='backup';type=$backup.type;command=$backup.command;state='off';status=$(if($backup.state-eq'Disabled'){'Выключено PC Guard'}else{'Проверь резервную запись'});source='Резервная копия PC Guard';path=$backup.path;size=$null;signature='Проверить файл';publisher='';protected=$false;originalPath=$backup.originalPath})
    }}catch{$issues.Add('Резервные записи: '+$_.Exception.Message)}
    return [pscustomobject]@{rows=@($rows.ToArray());issues=@($issues.ToArray());note='Цвет описывает разрешение запуска, а не вредоносность. Для служб и заданий доступен просмотр. Подпись их файлов проверяется в разделе «Анализ файла».'}
}

function Assert-GuardEntry($Entry) {
    if($Entry.kind-eq'registry'){
        $allowed=@(Get-GuardRegistryLocations|Where-Object{$_.hive-eq$Entry.hive-and$_.view-eq$Entry.view-and$_.key-eq$Entry.key})
        if($allowed.Count-ne1-or(Get-GuardRegistryId $Entry)-ne$Entry.id-or$Entry.valueKind-notin@(1,2)){throw 'Запись вне разрешённого списка автозапуска.'}
    }elseif($Entry.kind-eq'folder'){
        $full=[IO.Path]::GetFullPath($Entry.originalPath);$parent=[IO.Path]::GetDirectoryName($full)
        if((Get-GuardId $full)-ne$Entry.id-or-not(@(Get-GuardStartupFolders|Where-Object{$_-ieq$parent}))){throw 'Файл вне папки автозагрузки.'}
        $node=$parent
        while($node){if((Get-Item -LiteralPath $node -Force).Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Папка автозагрузки содержит ссылку.'};$node=[IO.Path]::GetDirectoryName($node)}
        if([IO.File]::Exists($full)-and((Get-Item -LiteralPath $full -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Файл автозагрузки является ссылкой.'}
    }else{throw 'Изменение этого типа не поддерживается.'}
    if(Test-GuardProtected ([string]$Entry.name) ([string]$Entry.command)){throw 'Изменение IObit, Defender и компонентов безопасности заблокировано в этой версии.'}
}

function Invoke-GuardStartupChange($Arguments,[bool]$Restore) {
    $lockPath=Join-Path $GuardData 'startup-mutation.lock';$lock=$null
    try{
        $lock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $entry=$Arguments.entry;$records=@(Get-GuardBackups)
        if($Restore){
            $match=@($records|Where-Object{$_.id-eq$entry.id-and$_.state-ne'Restored'});if($match.Count-ne1){throw 'Доступная резервная запись не найдена.'}
            $backup=$match[0];Assert-GuardEntry $backup
            if($backup.kind-eq'registry'){
                $base=Open-GuardRegistry $backup.hive $backup.view;$key=$null
                try{
                    $key=$base.CreateSubKey($backup.key)
                    if($key.GetValueNames()-contains$backup.name){
                        if($key.GetValue($backup.name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)-cne$backup.command-or[int]$key.GetValueKind($backup.name)-ne$backup.valueKind){throw 'На месте записи уже другое значение. Оно не перезаписано.'}
                    }else{$key.SetValue($backup.name,$backup.command,[Microsoft.Win32.RegistryValueKind]$backup.valueKind)}
                }finally{if($key){$key.Dispose()};$base.Dispose()}
            }else{
                $saved=[IO.Path]::GetFullPath($backup.savedPath);$allowedPrefix=[IO.Path]::GetFullPath((Join-Path $GuardData 'Backups'))+[IO.Path]::DirectorySeparatorChar
                if(-not$saved.StartsWith($allowedPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Неверный путь резервной копии.'}
                if((Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash-ne$backup.hash){throw 'Резервная копия повреждена.'}
                if([IO.File]::Exists($backup.originalPath)){if((Get-FileHash -LiteralPath $backup.originalPath -Algorithm SHA256).Hash-ne$backup.hash){throw 'На месте файла уже другой файл. Он не перезаписан.'}}
                else{[IO.File]::Copy($saved,$backup.originalPath,$false)}
            }
            $backup.state='Restored';$backup.updated=(Get-Date).ToString('o');Save-GuardBackups $records
            return @{id=$backup.id;name=$backup.name;restored=$true}
        }
        Assert-GuardEntry $entry
        if($records|Where-Object{$_.id-eq$entry.id-and$_.state-ne'Restored'}){throw 'Есть незавершённая резервная запись. Сначала выполни возврат.'}
        $backup=[ordered]@{};foreach($property in $entry.PSObject.Properties){$backup[$property.Name]=$property.Value}
        $backup.state='Prepared';$backup.updated=(Get-Date).ToString('o');$backup.token=[Guid]::NewGuid().ToString('N')
        if($entry.kind-eq'folder'){
            $directory=Join-Path $GuardData ('Backups\'+$backup.token);[void][IO.Directory]::CreateDirectory($directory)
            $saved=Join-Path $directory ([IO.Path]::GetFileName($entry.originalPath));[IO.File]::Copy($entry.originalPath,$saved,$false)
            $backup.savedPath=$saved;$backup.hash=(Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash
            if((Get-FileHash -LiteralPath $entry.originalPath -Algorithm SHA256).Hash-ne$backup.hash){throw 'Файл изменился при резервировании.'}
        }
        # Сначала записывается состояние Prepared: после аварии оригинал можно вернуть.
        $records=@($records|Where-Object{$_.id-ne$entry.id})+@([pscustomobject]$backup);Save-GuardBackups $records
        if($entry.kind-eq'registry'){
            $base=Open-GuardRegistry $entry.hive $entry.view;$key=$null
            try{
                $key=$base.OpenSubKey($entry.key,$true)
                if(-not$key-or$key.GetValue($entry.name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)-cne$entry.command-or[int]$key.GetValueKind($entry.name)-ne$entry.valueKind){throw 'Запись изменилась. Обнови список.'}
                $key.DeleteValue($entry.name,$true)
            }finally{if($key){$key.Dispose()};$base.Dispose()}
        }else{
            Assert-GuardEntry $entry
            if((Get-FileHash -LiteralPath $entry.originalPath -Algorithm SHA256).Hash-ne$backup.hash){throw 'Файл изменился перед отключением.'}
            [IO.File]::Delete($entry.originalPath)
        }
        foreach($record in $records){if($record.id-eq$entry.id){$record.state='Disabled'}};Save-GuardBackups $records
        return @{id=$entry.id;name=$entry.name;disabled=$true}
    }finally{if($lock){$lock.Dispose()}}
}
function Disable-GuardStartup($Arguments){return Invoke-GuardStartupChange $Arguments $false}
function Restore-GuardStartup($Arguments){return Invoke-GuardStartupChange $Arguments $true}
