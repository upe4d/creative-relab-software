# Пользовательские параметры пакетных приложений, без групповой политики и без изменения общего доступа.
if(-not(Get-Variable GuardCameraConsentRoot -Scope Script -ErrorAction SilentlyContinue)){$script:GuardCameraConsentRoot='HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam'}
function Open-GuardConsentKey([string]$Path){if(Test-Path -LiteralPath $Path -ErrorAction Stop){return Get-Item -LiteralPath $Path -ErrorAction Stop};return $null}
function Get-GuardCameraUserConsent {
    $records=@(Get-GuardJson (Join-Path $GuardData 'camera-user-consent.json') @())
    $global=Open-GuardConsentKey $script:GuardCameraConsentRoot
    try{$globalValue=Read-GuardCameraPolicyValue $global 'Value'}finally{if($global){$global.Dispose()}}
    $rows=@(Get-GuardPackagedCameraApps|ForEach-Object{
        $app=$_;$key=Open-GuardConsentKey (Join-Path $script:GuardCameraConsentRoot $app.family)
        try{$value=Read-GuardCameraPolicyValue $key 'Value'}finally{if($key){$key.Dispose()}}
        $ours=@($records|Where-Object{$_.family-ceq$app.family-and$_.state-eq'Applied'})
        [pscustomobject]@{name=$app.name;family=$app.family;denied=($value.value-ceq'Deny');ownDenied=($ours.Count-eq1-and$value.value-ceq'Deny');choice=$value.value}
    })
    return [pscustomobject]@{rows=$rows;supported=$true;mode='current-user-consent';requiresAdmin=$false;manifestReadErrors=[int]$script:CameraAppReadErrors;globalChoice=$globalValue;scope='Пользовательские разрешения приложений изолированной среды Windows';classicExeSupported=$false;captureEnforcementTested=$false;note='Только текущая учётная запись. Общие запреты не меняются. Для применения перезапусти приложение. Возврат восстанавливает исходное значение, не отменяя другие ограничения.'}
}
function Set-GuardCameraUserConsent($Arguments,[bool]$Restore){
    $family=[string]$Arguments.family
    if($family-notmatch'\A[A-Za-z0-9][A-Za-z0-9.\-]{0,180}_[A-Za-z0-9]{8,20}\z'){throw 'Нужен идентификатор пакетного приложения, не путь EXE.'}
    if(-not@((Get-GuardPackagedCameraApps)|Where-Object{$_.family-ceq$family}).Count){throw 'Пакет с камерой отсутствует в актуальном списке.'}
    $path=Join-Path $script:GuardCameraConsentRoot $family;$key=Open-GuardConsentKey $path
    try{$before=Read-GuardCameraPolicyValue $key 'Value'}finally{if($key){$key.Dispose()}}
    if($before.exists-and($before.kind-ne1-or$before.value-cnotin@('Allow','Deny','Prompt'))){throw 'Неизвестное значение пользовательского разрешения; изменение отменено.'}
    $file=Join-Path $GuardData 'camera-user-consent.json';$records=@(Get-GuardJson $file @());$found=@($records|Where-Object{$_.family-ceq$family-and$_.state-eq'Applied'})
    if($Restore){
        if($found.Count-ne1){throw 'Нет собственного изменения для возврата.'}
        $record=$found[0]
        if(($before|ConvertTo-Json -Compress -Depth 4)-cne($record.applied|ConvertTo-Json -Compress -Depth 4)){throw 'Разрешение изменено извне. Возврат отменён.'}
        $next=$record.original
    }else{
        if($found.Count){throw 'Уже есть собственный запрет; сначала верни прежнее состояние.'}
        if($before.value-ceq'Deny'){throw 'Запрет уже был установлен вне PC Guard; он сохранён.'}
        $next=[pscustomobject]@{exists=$true;kind=1;value='Deny'}
        $record=[pscustomobject]@{family=$family;state='Prepared';original=$before;applied=$next}
        $records=@($records|Where-Object{$_.family-cne$family})+@($record)
    }
    $record.state='Prepared';Write-GuardJson $file @($records)
    if(-not(Test-Path -LiteralPath $path)){[void](New-Item -Path $path -Force)}
    $readonly=Get-Item -LiteralPath $path;try{$key=$readonly.OpenSubKey('',$true)}finally{$readonly.Dispose()}
    try{
        Set-GuardCameraPolicyValue $key 'Value' $next
        $actual=Read-GuardCameraPolicyValue $key 'Value'
        if(($actual|ConvertTo-Json -Compress -Depth 4)-cne($next|ConvertTo-Json -Compress -Depth 4)){throw 'Windows не подтвердила запись пользовательского параметра.'}
        $record.state=$(if($Restore){'Restored'}else{'Applied'});Write-GuardJson $file @($records)
    }catch{
        try{Set-GuardCameraPolicyValue $key 'Value' $before;$record.state=$(if($Restore){'Applied'}else{'NotApplied'});Write-GuardJson $file @($records)}catch{}
        throw
    }finally{$key.Dispose()}
    return Get-GuardCameraUserConsent
}
