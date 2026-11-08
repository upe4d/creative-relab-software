$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'system\common.ps1')
$GuardData=Join-Path $GuardData ('Tests\camera-'+[Guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($GuardData)
$script:GuardCameraPolicyRoot='HKCU:\Software\PCGuard\PolicyTests\'+[Guid]::NewGuid().ToString('N')
$script:GuardCameraPolicyFixture=$true
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'system\camera-policy.ps1')
function Get-GuardPackagedCameraApps{return @([pscustomobject]@{family='PCGuard.CameraFixture_123456789abcd';name='Fixture'},[pscustomobject]@{family='PCGuard.SecondFixture_123456789abcd';name='Second fixture'})}
$checks=New-Object 'Collections.Generic.List[string]'
function Check([bool]$value,[string]$name){if(-not$value){throw $name};$checks.Add($name)}
function Fail([scriptblock]$action,[string]$name){$failed=$false;try{&$action|Out-Null}catch{$failed=$true};Check $failed $name}
$first=[pscustomobject]@{family='PCGuard.CameraFixture_123456789abcd'};$second=[pscustomobject]@{family='PCGuard.SecondFixture_123456789abcd'}
Fail {Set-GuardCameraAppPolicy ([pscustomobject]@{family='C:\untrusted.exe'}) $false} 'EXE не выдаётся за пакетное приложение'
Fail {Set-GuardCameraAppPolicy ([pscustomobject]@{family='Missing.Package_123456789abcd'}) $false} 'Отсутствующее семейство отклонено'
$result=Set-GuardCameraAppPolicy $first $false
Check (@($result.rows|Where-Object{$_.family-eq$first.family-and$_.denied}).Count-eq1) 'Выборочный запрет внесён для тестового пакета'
$key=Get-Item -LiteralPath $script:GuardCameraPolicyRoot
Check ([int]$key.GetValueKind('LetAppsAccessCamera_ForceDenyTheseApps')-eq7) 'Тип политики соответствует multiText ADMX'
Check ($key.GetValue('LetAppsAccessCamera')-eq0) 'Глобальный режим остаётся под управлением владельца'
$key.Dispose()
[void](Set-GuardCameraAppPolicy $second $false);$result=Set-GuardCameraAppPolicy $first $true
Check (@($result.rows|Where-Object{$_.family-eq$second.family-and$_.denied}).Count-eq1) 'Возврат одного пакета сохраняет запрет другого'
[void](Set-GuardCameraAppPolicy $second $true);$key=Get-Item -LiteralPath $script:GuardCameraPolicyRoot
Check (-not($key.GetValueNames()-contains'LetAppsAccessCamera_ForceDenyTheseApps')) 'Пустое исходное состояние возвращено без остаточного запрета'
Check (-not($key.GetValueNames()-contains'LetAppsAccessCamera')) 'Созданное значение default удалено при полном возврате'
$key.Dispose()
$readonly=Get-Item -LiteralPath $script:GuardCameraPolicyRoot;$key=$readonly.OpenSubKey('',$true);$readonly.Dispose()
$key.SetValue('LetAppsAccessCamera_ForceDenyTheseApps',[string[]]@('External.Package_123456789abcd'),[Microsoft.Win32.RegistryValueKind]::MultiString)
$key.SetValue('LetAppsAccessCamera',2,[Microsoft.Win32.RegistryValueKind]::DWord);$key.Dispose()
[void](Set-GuardCameraAppPolicy $first $false);[void](Set-GuardCameraAppPolicy $first $true)
$key=Get-Item -LiteralPath $script:GuardCameraPolicyRoot
Check ($key.GetValue('LetAppsAccessCamera')-eq2) 'Внешний общий запрет не ослаблен'
Check (@($key.GetValue('LetAppsAccessCamera_ForceDenyTheseApps'))-contains'External.Package_123456789abcd') 'Внешний список запретов сохранён'
$key.Dispose()
[void](Set-GuardCameraAppPolicy $first $false)
$readonly=Get-Item -LiteralPath $script:GuardCameraPolicyRoot;$key=$readonly.OpenSubKey('',$true);$readonly.Dispose();$key.SetValue('LetAppsAccessCamera',1,[Microsoft.Win32.RegistryValueKind]::DWord);$key.Dispose()
Fail {Set-GuardCameraAppPolicy $first $true} 'Конфликтующая внешняя правка не перезаписывается'
Check (-not(Get-GuardCameraPolicy).classicExeSupported) 'Классические EXE не объявлены защищёнными'
$readonly=Get-Item -LiteralPath $script:GuardCameraPolicyRoot;$key=$readonly.OpenSubKey('',$true);$readonly.Dispose();$key.SetValue('LetAppsAccessCamera',2,[Microsoft.Win32.RegistryValueKind]::DWord);$key.Dispose()
[void](Set-GuardCameraAppPolicy $first $true)
$readonly=Get-Item -LiteralPath $script:GuardCameraPolicyRoot;$key=$readonly.OpenSubKey('',$true);$readonly.Dispose();$key.DeleteValue('LetAppsAccessCamera');$key.Dispose()
$script:OriginalCameraSetter=(Get-Command Set-GuardCameraPolicyValue).ScriptBlock;$script:CameraInjectFailure=$true
function Set-GuardCameraPolicyValue($Key,[string]$Name,$Value){if($script:CameraInjectFailure-and$Name-eq'LetAppsAccessCamera_ForceDenyTheseApps'){$script:CameraInjectFailure=$false;throw 'Искусственный отказ записи тестовой политики'};& $script:OriginalCameraSetter $Key $Name $Value}
Fail {Set-GuardCameraAppPolicy $first $false} 'Частичная ошибка записи явно возвращается'
$key=Get-Item -LiteralPath $script:GuardCameraPolicyRoot
Check (-not($key.GetValueNames()-contains'LetAppsAccessCamera')) 'После ошибки восстановлено отсутствие default'
Check (@($key.GetValue('LetAppsAccessCamera_ForceDenyTheseApps'))-contains'External.Package_123456789abcd') 'После ошибки внешний запрет сохранён'
$key.Dispose()
$saved=Get-GuardJson (Join-Path $GuardData 'camera-app-policy-backup.json') $null
Check ($saved.state-eq'Restored'-and@($saved.ownFamilies).Count-eq0) 'После отката журнал не выдаёт запрет за применённый'
[void](Set-GuardCameraAppPolicy $first $false);[void](Set-GuardCameraAppPolicy $first $true)
Check (@((Get-GuardCameraPolicy).rows|Where-Object{$_.ownDenied}).Count-eq0) 'После отказа доступны повторная операция и возврат'
$report=[pscustomobject]@{checks=$checks.Count;results=@($checks.ToArray());fixtureRegistry=$script:GuardCameraPolicyRoot;realCameraPolicyChanged=$false;physicalCaptureTested=$false}
Write-GuardJson (Join-Path (Split-Path -Parent $GuardData) 'camera-policy-results.json') $report
$report|ConvertTo-Json -Depth 4
