param([string]$Toolchain='D:\Codex\work\PCGuard-toolchain')
$ErrorActionPreference='Stop'
# Установка допускается только после внешней подписи и испытаний. Режим загрузки ОС не меняется.
$release=(& (Join-Path $PSScriptRoot 'Check-Release.ps1') -Toolchain $Toolchain)|ConvertFrom-Json
if(-not$release.ReadyForInstallation){throw 'Установка не выполнена: нужна подпись по kernel policy, назначенная высота minifilter и подтверждённые испытания в ядре. Подробности: build\release-check.json.'}
$binary=[IO.Path]::GetFullPath($release.Binary)
if([IO.Path]::GetPathRoot($binary)-ine'D:\'){throw 'Драйвер должен находиться на D.'}
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Для установки собственного драйвера нужны права администратора.'}
$service='HKLM:\SYSTEM\CurrentControlSet\Services\PCGuardFilter'
if(Test-Path -LiteralPath $service){throw 'Служба PCGuardFilter уже существует. Автоматическая замена отменена.'}
& sc.exe create PCGuardFilter type= filesys start= demand error= normal binPath= ('"'+$binary+'"') group= 'FSFilter Anti-Virus' depend= FltMgr
if($LASTEXITCODE-ne0){throw 'Windows не создала службу собственного фильтра.'}
$instance=Join-Path $service 'Parameters\Instances\PCGuard Instance'
[void](New-Item -Path $instance -Force)
[void](New-ItemProperty -LiteralPath (Join-Path $service 'Parameters\Instances') -Name DefaultInstance -Value 'PCGuard Instance' -PropertyType String -Force)
[void](New-ItemProperty -LiteralPath $instance -Name Altitude -Value $release.AssignedAltitude -PropertyType String -Force)
[void](New-ItemProperty -LiteralPath $instance -Name Flags -Value 0 -PropertyType DWord -Force)
& sc.exe start PCGuardFilter
if($LASTEXITCODE-ne0){throw 'Служба создана, но Windows не загрузила фильтр. Другие драйверы и режим загрузки не изменены.'}
Write-Output 'Собственный фильтр загружен без активных правил. Политика включается отдельно в PC Guard.'
