$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'system\common.ps1')
$GuardData=Join-Path $GuardData ('Tests\consent-'+[Guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($GuardData)
$script:GuardCameraConsentRoot='HKCU:\Software\PCGuard\ConsentTests\'+[Guid]::NewGuid().ToString('N')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'system\camera-policy.ps1')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'system\camera-consent.ps1')
function Get-GuardPackagedCameraApps{return @([pscustomobject]@{family='PCGuard.CameraFixture_123456789abcd';name='Fixture'},[pscustomobject]@{family='PCGuard.SecondFixture_123456789abcd';name='Second'})}
$checks=New-Object 'Collections.Generic.List[string]'
function Check([bool]$v,[string]$name){if(-not$v){throw $name};$checks.Add($name)}
function Fail([scriptblock]$code,[string]$name){$failed=$false;try{&$code|Out-Null}catch{$failed=$true};Check $failed $name}
Check (Test-GuardIsolatedCameraManifest ([xml]'<Package><Applications><Application EntryPoint="Camera.App"/></Applications><Capabilities><DeviceCapability Name="webcam"/></Capabilities></Package>')) 'Приложение изолированной среды с камерой распознано'
Check (-not(Test-GuardIsolatedCameraManifest ([xml]'<Package><Applications><Application EntryPoint="Windows.FullTrustApplication"/></Applications><Capabilities><DeviceCapability Name="webcam"/></Capabilities></Package>'))) 'Пакетный desktop EXE не выдаётся за изолированное приложение'
Check (-not(Test-GuardIsolatedCameraManifest ([xml]'<Package><Applications><Application EntryPoint="Camera.App"/></Applications><Capabilities><DeviceCapability Name="webcam"/><Capability Name="runFullTrust"/></Capabilities></Package>'))) 'Пакет с full-trust помощником исключён из обещания выборочного запрета'
$first=[pscustomobject]@{family='PCGuard.CameraFixture_123456789abcd'};$second=[pscustomobject]@{family='PCGuard.SecondFixture_123456789abcd'}
Fail {Set-GuardCameraUserConsent ([pscustomobject]@{family='C:\fake.exe'}) $false} 'Произвольный EXE отклонён'
[void](Set-GuardCameraUserConsent $first $false)
$r=Get-GuardCameraUserConsent;Check (@($r.rows|Where-Object{$_.family-eq$first.family-and$_.ownDenied}).Count-eq1) 'Параметр запрета внесён в тестовый профиль'
Check (@($r.rows|Where-Object{$_.family-eq$second.family-and-not$_.denied}).Count-eq1) 'Разрешение второго пакета не меняется'
Check (-not$r.requiresAdmin) 'Пользовательское разрешение не требует групповой политики'
[void](Set-GuardCameraUserConsent $first $true)
$key=Get-Item -LiteralPath (Join-Path $script:GuardCameraConsentRoot $first.family)
Check (-not($key.GetValueNames()-contains'Value')) 'Отсутствовавшее значение не заменяется принудительным Allow'
$w=$key.OpenSubKey('',$true);$key.Dispose();$w.SetValue('Value','Allow',[Microsoft.Win32.RegistryValueKind]::String);$w.SetValue('LastUsedTimeStart',[long]42,[Microsoft.Win32.RegistryValueKind]::QWord);$w.Dispose()
[void](Set-GuardCameraUserConsent $first $false);[void](Set-GuardCameraUserConsent $first $true)
$key=Get-Item -LiteralPath (Join-Path $script:GuardCameraConsentRoot $first.family)
Check ($key.GetValue('Value')-ceq'Allow') 'Исходное Allow и тип возвращены'
Check ($key.GetValue('LastUsedTimeStart')-eq42) 'История использования не изменена'
$key.Dispose();[void](Set-GuardCameraUserConsent $first $false)
$read=Get-Item -LiteralPath (Join-Path $script:GuardCameraConsentRoot $first.family);$key=$read.OpenSubKey('',$true);$read.Dispose();$key.SetValue('Value','Prompt',[Microsoft.Win32.RegistryValueKind]::String);$key.Dispose()
Fail {Set-GuardCameraUserConsent $first $true} 'Внешняя правка не перезаписывается при возврате'
Check (-not(Get-GuardCameraUserConsent).classicExeSupported) 'Права классических EXE не заявлены'
Check (-not(Get-GuardCameraUserConsent).captureEnforcementTested) 'Проверка записи не выдаётся за физическое испытание камеры'
$report=[pscustomobject]@{checks=$checks.Count;results=$checks.ToArray();fixture=$script:GuardCameraConsentRoot;realCameraConsentChanged=$false;physicalCaptureTested=$false}
Write-GuardJson (Join-Path (Split-Path -Parent $GuardData) 'camera-consent-results.json') $report
$report|ConvertTo-Json -Depth 4
