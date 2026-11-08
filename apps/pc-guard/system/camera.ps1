# Камера: чтение устройств и записей Windows. Отключение/возврат — только по явной команде.
function Get-GuardCameraDevices {
    return @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -eq 'Camera' -or ($_.PNPClass -eq 'Image' -and $_.Name -match '(?i)camera|webcam|камера') } | ForEach-Object {
        [pscustomobject]@{id=$_.PNPDeviceID;name=$_.Name;code=[int]$_.ConfigManagerErrorCode;status=$(if($_.ConfigManagerErrorCode-eq22){'Отключено'}elseif($_.ConfigManagerErrorCode-eq0){'Доступно'}else{'Ошибка устройства: '+$_.ConfigManagerErrorCode})}
    })
}
function Get-GuardCameras {
    $devices=Get-GuardCameraDevices;$apps=New-Object 'Collections.Generic.List[object]';$issues=New-Object 'Collections.Generic.List[string]'
    $root='HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam'
    if(Test-Path -LiteralPath $root){
        foreach($key in @(Get-ChildItem -LiteralPath $root -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1000)){
            try{
                $values=Get-ItemProperty -LiteralPath $key.PSPath
                if($values.PSObject.Properties.Name -notcontains 'LastUsedTimeStart'){continue}
                $start=$null;$stop=$null
                if([long]$values.LastUsedTimeStart-gt0){$start=[DateTime]::FromFileTimeUtc([long]$values.LastUsedTimeStart).ToString('o')}
                if([long]$values.LastUsedTimeStop-gt0){$stop=[DateTime]::FromFileTimeUtc([long]$values.LastUsedTimeStop).ToString('o')}
                $apps.Add([pscustomobject]@{name=$key.PSChildName.Replace('#','\');start=$start;stop=$stop;consent=[string]$values.Value;note='Запись Windows об использовании; не подтверждает текущий захват камеры'})
            }catch{$issues.Add($_.Exception.Message)}
        }
    }
    return [pscustomobject]@{devices=@($devices);apps=@($apps.ToArray());issues=@($issues.ToArray());perAppEnforcement=$false;perAppReason='Собственный выборочный перехват процессов не установлен. Разрешения приложений, поддерживаемые этой Windows, доступны в её настройках.';backups=@(Get-GuardJson (Join-Path $GuardData 'camera-backups.json') @())}
}
function Set-GuardCamera($Arguments,[bool]$Restore){
    $id=[string]$Arguments.id
    if($id.Length-lt3-or$id.Length-gt1000-or$id-match'[\x00-\x1F]'){throw 'Некорректный идентификатор камеры.'}
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if(-not$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Для изменения устройства требуется запуск PC Guard от имени администратора.'}
    $devices=@(Get-GuardCameraDevices|Where-Object{$_.id-ceq$id});if($devices.Count-ne1){throw 'Камера отсутствует в актуальном списке.'}
    $path=Join-Path $GuardData 'camera-backups.json';$records=@(Get-GuardJson $path @());$existing=@($records|Where-Object{$_.id-ceq$id-and$_.state-ne'Restored'})
    if($Restore){
        if($existing.Count-ne1-or$existing[0].originalCode-ne0){throw 'Нет записи PC Guard о ранее доступной камере.'}
        Enable-PnpDevice -InstanceId $id -Confirm:$false
        $fresh=@(Get-GuardCameraDevices|Where-Object{$_.id-ceq$id})
        if($fresh.Count-ne1-or$fresh[0].code-ne0){throw 'Windows не подтвердила включение камеры. Проверь диспетчер устройств; может требоваться перезагрузка.'}
        $existing[0].state='Restored';Write-GuardJson $path @($records)
    }else{
        if($devices[0].code-ne0){throw 'Камера уже выключена или содержит ошибку. PC Guard её не изменял.'}
        if($existing.Count){throw 'Есть незавершённая запись. Сначала выполни возврат устройства.'}
        $backup=[pscustomobject]@{id=$id;name=$devices[0].name;originalCode=$devices[0].code;date=(Get-Date).ToString('o');state='Prepared'}
        $records=@($records|Where-Object{$_.id-cne$id})+@($backup);Write-GuardJson $path $records
        Disable-PnpDevice -InstanceId $id -Confirm:$false
        $fresh=@(Get-GuardCameraDevices|Where-Object{$_.id-ceq$id})
        if($fresh.Count-ne1-or$fresh[0].code-ne22){throw 'Windows не подтвердила отключение камеры. Проверь диспетчер устройств; может требоваться перезагрузка.'}
        $backup.state='Disabled';Write-GuardJson $path $records
    }
    return Get-GuardCameras
}
