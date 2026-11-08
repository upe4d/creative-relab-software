# Поддерживаемая Windows политика для пакетных приложений. Классические EXE сюда не подставляются.
if(-not(Get-Variable GuardCameraPolicyRoot -Scope Script -ErrorAction SilentlyContinue)){$script:GuardCameraPolicyRoot='HKLM:\Software\Policies\Microsoft\Windows\AppPrivacy'}
function Test-GuardIsolatedCameraManifest($Manifest){
    if($null-eq$Manifest){return $false}
    $webcam=@($Manifest.SelectNodes('//*[local-name()="DeviceCapability" and @Name="webcam"]')).Count-gt0
    $fullTrust=@($Manifest.SelectNodes('//*[local-name()="Capability" and @Name="runFullTrust"]')).Count-gt0
    $desktop=@($Manifest.SelectNodes('//*[local-name()="Application"]')|Where-Object{$_.GetAttribute('EntryPoint')-eq'Windows.FullTrustApplication'-or@($_.Attributes|Where-Object{$_.LocalName-eq'RuntimeBehavior'-and$_.Value-match'packagedClassicApp|win32App|desktopApp'}).Count-gt0}).Count-gt0
    return $webcam-and-not$fullTrust-and-not$desktop
}
function Get-GuardPackagedCameraApps {
    $script:CameraAppReadErrors=0
    return @(Get-AppxPackage|Where-Object{-not$_.NonRemovable-and-not$_.IsFramework}|ForEach-Object{
        try{$manifest=Get-AppxPackageManifest -Package $_.PackageFullName -ErrorAction Stop
            if(Test-GuardIsolatedCameraManifest $manifest){[pscustomobject]@{family=$_.PackageFamilyName;name=$_.Name}}
        }catch{$script:CameraAppReadErrors++}
    })
}
function Open-GuardCameraPolicyReadonly {if(Test-Path -LiteralPath $script:GuardCameraPolicyRoot -ErrorAction Stop){return Get-Item -LiteralPath $script:GuardCameraPolicyRoot -ErrorAction Stop};return $null}
function Read-GuardCameraPolicyValue($Key,[string]$Name){
    if($null-eq$Key-or-not($Key.GetValueNames()-contains$Name)){return [pscustomobject]@{exists=$false;kind=$null;value=$null}}
    return [pscustomobject]@{exists=$true;kind=[int]$Key.GetValueKind($Name);value=$Key.GetValue($Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
}
function Set-GuardCameraPolicyValue($Key,[string]$Name,$Value){
    if(-not$Value.exists){$Key.DeleteValue($Name,$false);return}
    $data=$Value.value;if([int]$Value.kind-eq7){$data=[string[]]@($Value.value)}
    $Key.SetValue($Name,$data,[Microsoft.Win32.RegistryValueKind][int]$Value.kind)
}
function Get-GuardCameraPolicy {
    $key=Open-GuardCameraPolicyReadonly
    try{$denied=Read-GuardCameraPolicyValue $key 'LetAppsAccessCamera_ForceDenyTheseApps';$base=Read-GuardCameraPolicyValue $key 'LetAppsAccessCamera'}finally{if($key){$key.Dispose()}}
    $backup=Get-GuardJson (Join-Path $GuardData 'camera-app-policy-backup.json') $null
    $rows=@(Get-GuardPackagedCameraApps|ForEach-Object{[pscustomobject]@{name=$_.name;family=$_.family;denied=(@($denied.value)-contains$_.family);ownDenied=($backup-and@($backup.ownFamilies)-contains$_.family)}})
    $readErrors=[int]$script:CameraAppReadErrors
    $edition=(Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
    return [pscustomobject]@{rows=$rows;supported=($edition-match'Professional|Enterprise|Education|IoT');manifestReadErrors=$readErrors;scope='Только доступные пакетные приложения с объявленной камерой';classicExeSupported=$false;defaultPolicy=$base;note='Снятие запрета PC Guard возвращает прежнюю политику и не отменяет другие запреты. Для применения перезапусти приложение.'}
}
function Set-GuardCameraAppPolicy($Arguments,[bool]$Restore){
    $family=[string]$Arguments.family
    if($family-notmatch'\A[A-Za-z0-9][A-Za-z0-9.\-]{0,180}_[A-Za-z0-9]{8,20}\z'){throw 'Некорректное семейство пакетного приложения.'}
    if(-not@((Get-GuardPackagedCameraApps)|Where-Object{$_.family-ceq$family}).Count){throw 'Приложение с камерой отсутствует в актуальном списке.'}
    if(-not$script:GuardCameraPolicyFixture){
        $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        if(-not$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Для политики камеры нужны права администратора.'}
        if(-not(Get-GuardCameraPolicy).supported){throw 'Эта редакция Windows не подтверждает поддержку политики.'}
    }
    $file=Join-Path $GuardData 'camera-app-policy-backup.json';$backup=Get-GuardJson $file $null
    $key=Open-GuardCameraPolicyReadonly
    try{$beforeDeny=Read-GuardCameraPolicyValue $key 'LetAppsAccessCamera_ForceDenyTheseApps';$beforeDefault=Read-GuardCameraPolicyValue $key 'LetAppsAccessCamera'}finally{if($key){$key.Dispose()}}
    if($beforeDefault.exists-and($beforeDefault.kind-ne4-or$beforeDefault.value-notin@(0,1,2))){throw 'Неизвестный формат внешней общей политики. Изменение отменено.'}
    if($beforeDeny.exists-and$beforeDeny.kind-ne7){throw 'Внешняя политика имеет другой тип. PC Guard её не меняет.'}
    if($backup-and$backup.state-ne'Restored'){
        if(($beforeDeny|ConvertTo-Json -Compress -Depth 5)-cne($backup.appliedDeny|ConvertTo-Json -Compress -Depth 5)-or($beforeDefault|ConvertTo-Json -Compress -Depth 5)-cne($backup.appliedDefault|ConvertTo-Json -Compress -Depth 5)){throw 'Политика изменена извне. Автоматическая перезапись отменена.'}
    }else{
        if($Restore){throw 'Нет собственного запрета для возврата.'}
        $backup=[pscustomobject]@{state='Prepared';originalDeny=$beforeDeny;originalDefault=$beforeDefault;ownFamilies=@();appliedDeny=$beforeDeny;appliedDefault=$beforeDefault}
    }
    $previousOwn=@($backup.ownFamilies);$own=@($backup.ownFamilies)
    if($Restore){if($own-notcontains$family){throw 'Этот запрет не был добавлен PC Guard.'};$own=@($own|Where-Object{$_-cne$family})}
    else{if(@($backup.originalDeny.value)-contains$family){throw 'Приложение уже запрещено внешней политикой.'};$own=@(($own+@($family))|Select-Object -Unique)}
    $nextDeny=$backup.originalDeny;$nextDefault=$backup.originalDefault
    if($own.Count){$nextDeny=[pscustomobject]@{exists=$true;kind=7;value=[string[]]@(@($backup.originalDeny.value|Where-Object{$null-ne$_})+$own|Select-Object -Unique)};if(-not$nextDefault.exists){$nextDefault=[pscustomobject]@{exists=$true;kind=4;value=0}}}
    $backup.state='Prepared';$backup.ownFamilies=$own;$backup.appliedDeny=$nextDeny;$backup.appliedDefault=$nextDefault
    Write-GuardJson $file $backup
    if(-not(Test-Path -LiteralPath $script:GuardCameraPolicyRoot)){[void](New-Item -Path $script:GuardCameraPolicyRoot -Force)}
    $readonly=Get-Item -LiteralPath $script:GuardCameraPolicyRoot
    try{$key=$readonly.OpenSubKey('',$true)}finally{$readonly.Dispose()}
    try{
        Set-GuardCameraPolicyValue $key 'LetAppsAccessCamera' $nextDefault
        Set-GuardCameraPolicyValue $key 'LetAppsAccessCamera_ForceDenyTheseApps' $nextDeny
        $actual=Read-GuardCameraPolicyValue $key 'LetAppsAccessCamera_ForceDenyTheseApps'
        if(($actual|ConvertTo-Json -Compress -Depth 5)-cne($nextDeny|ConvertTo-Json -Compress -Depth 5)){throw 'Реестр не подтвердил новую политику.'}
        $backup.state=$(if($own.Count){'Applied'}else{'Restored'});Write-GuardJson $file $backup
    }catch{
        try{Set-GuardCameraPolicyValue $key 'LetAppsAccessCamera_ForceDenyTheseApps' $beforeDeny;Set-GuardCameraPolicyValue $key 'LetAppsAccessCamera' $beforeDefault
            $backup.ownFamilies=$previousOwn;$backup.appliedDeny=$beforeDeny;$backup.appliedDefault=$beforeDefault;$backup.state=$(if($previousOwn.Count){'Applied'}else{'Restored'});Write-GuardJson $file $backup
        }catch{}
        throw
    }finally{$key.Dispose()}
    return Get-GuardCameraPolicy
}
