param([string]$Build='D:\Codex\work\PCGuard-v5-build',[string]$Destination='D:\PC Guard')
$ErrorActionPreference='Stop'
# Обновление сохраняет Data и резервирует предыдущие файлы приложения только на D.
$target=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
if($target-ine'D:\PC Guard'){throw 'Допускается только D:\PC Guard.'}
foreach($directory in @($target,$Build)){
    $item=Get-Item -LiteralPath $directory
    if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Каталог не должен быть ссылкой.'}
}
$names=@('PC Guard.exe','app.ico','app.manifest','native','public','system','tests','Build-Native.ps1','README.txt','ROADMAP.md','THIRD-PARTY.txt','rules','driver','Install-Update.ps1','Build-Indicators.py')
foreach($name in $names){if(-not(Test-Path -LiteralPath (Join-Path $Build $name))){throw ('Нет файла сборки: '+$name)}}
$reports=@('native-results.json','startup-results.json','suite-results.json','browser-results.json','suite-browser-results.json','camera-policy-results.json','camera-consent-results.json','provider-results.json')
$counts=@{}
foreach($name in $reports){
    $report=Get-Content -LiteralPath (Join-Path $Build ('Data\Tests\'+$name)) -Raw|ConvertFrom-Json
    if($report.checks-lt1-or@($report.errors).Where({$null-ne$_}).Count){throw ('Не пройдены проверки: '+$name)}
    $counts[$name]=[int]$report.checks
}
$exe=Join-Path $target 'PC Guard.exe'
# Останавливаем только процесс из каталога установки, используя его локальный ключ.
$runtimePath=Join-Path $target 'Data\runtime.json'
if(Test-Path -LiteralPath $runtimePath){
    $runtime=Get-Content -LiteralPath $runtimePath -Raw|ConvertFrom-Json
    $running=Get-Process -Id $runtime.pid -ErrorAction SilentlyContinue
    if($running){
        if($running.Path-ine$exe){throw 'PID файла запуска относится к другой программе.'}
        $reply=Invoke-RestMethod -Uri ('http://127.0.0.1:'+$runtime.port+'/api') -Method Post -Headers @{'X-PCGuard-Token'=$runtime.token} -ContentType 'application/json' -Body '{"action":"shutdown"}' -TimeoutSec 10
        if(-not$reply.ok){throw 'Приложение отказало в остановке.'}
        if(-not$running.WaitForExit(15000)){throw 'PC Guard не завершился; обновление прервано.'}
    }
}
$backup=Join-Path $target ('Backups\'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-before-3.3')
[void][IO.Directory]::CreateDirectory($backup)
foreach($name in ($names+@('VERIFICATION.json'))){
    $existing=Join-Path $target $name
    if(Test-Path -LiteralPath $existing){Copy-Item -LiteralPath $existing -Destination $backup -Recurse -Force}
}
$beforeHash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
# При возврате к 2.0 её движок не должен получить новые предупреждающие IOC как блокирующие.
$engineBackup=Join-Path $backup 'EngineState';[void][IO.Directory]::CreateDirectory($engineBackup)
foreach($stateName in @('engine-rules.json','engine-seed-state.json')){
    $statePath=Join-Path $target ('Data\'+$stateName)
    if(Test-Path -LiteralPath $statePath){Copy-Item -LiteralPath $statePath -Destination $engineBackup -Force}
}
[IO.File]::WriteAllText((Join-Path $engineBackup 'RESTORE.txt'),'При откате к 2.0 восстанови engine-rules.json в Data вместе с файлами программы: старый движок не различает предупреждающие и блокирующие IOC. Если engine-seed-state.json отсутствует в этой резервной копии, запиши в Data\engine-seed-state.json объект {"version":""} перед последующим обновлением до 3.0. Ключи сейфа и остальные пользовательские данные не копировались.',[Text.UTF8Encoding]::new($false))
try{
    foreach($name in $names|Where-Object{$_-ne'PC Guard.exe'}){Copy-Item -LiteralPath (Join-Path $Build $name) -Destination $target -Recurse -Force}
    # Замена EXE атомарна; дополнительная копия старого EXE остаётся для возврата.
    $next=Join-Path $target 'PC Guard.next.exe'
    Copy-Item -LiteralPath (Join-Path $Build 'PC Guard.exe') -Destination $next -Force
    [IO.File]::Replace($next,$exe,(Join-Path $backup 'PC Guard.atomic-backup.exe'))
    $expected=(Get-FileHash -LiteralPath (Join-Path $Build 'PC Guard.exe') -Algorithm SHA256).Hash
    if((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash-ne$expected){throw 'SHA-256 установленного EXE не совпал со сборкой.'}
    $evidence=Join-Path $target ('Data\Verification\'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-3.3')
    [void][IO.Directory]::CreateDirectory($evidence)
    foreach($name in $reports){Copy-Item -LiteralPath (Join-Path $Build ('Data\Tests\'+$name)) -Destination $evidence -Force}
    Copy-Item -LiteralPath (Join-Path $Build 'Data\tray-test.json') -Destination $evidence -Force
    foreach($name in @('suite-engine.png','suite-vault.png','suite-driver.png','suite-camera.png','dropdowns-200.png','dropdown-layout.json','partner-dark-1160.png','partner-dark-375.png','partner-light-1160.png','partner-light-375.png')){Copy-Item -LiteralPath (Join-Path $Build ('Data\Tests\'+$name)) -Destination $evidence -Force}
    $driverCheck=Get-Content -LiteralPath (Join-Path $Build 'driver\build\release-check.json') -Raw|ConvertFrom-Json
    $policyTests=Get-Content -LiteralPath (Join-Path $Build 'driver\build\policy-test-results.json') -Raw|ConvertFrom-Json
    $verification=[ordered]@{
        Version='3.3.0';Installed=$target;Updated=(Get-Date).ToString('o');SHA256=$expected;PreviousSHA256=$beforeHash;PreviousVersionBackup=$backup
        Checks=$counts;AutomatedChecks=($counts.Values|Measure-Object -Sum).Sum;Evidence=$evidence;TrayInitialized=$true
        DataPreserved=$true;DefenderSwitched=$false;IObitSwitched=$false;CameraToggled=$false;RealMalwareExecuted=$false;OwnAutostartEnabledByInstaller=$false
        OwnEngine=[ordered]@{PublishedSha256Rules=19788;BlockRules=19216;CampaignAlertRules=572;MostlyHistorical=$true;BroadAntivirusCoverage=$false;ArchiveOrMemoryAnalysis=$false}
        Realtime=[ordered]@{AfterFileEvent=$true;DefaultRoot=(Join-Path $target 'Incoming');PreExecutionBlocking=$false}
        Vault=[ordered]@{RoundtripWrongPasswordTamperVerified=$true;OriginalRemoved=$false;IndependentCryptoAudit=$false}
        FolderProtection=[ordered]@{EncryptedBackupsAndReadonlyHandlesVerified=$true;SystemBehavioralRansomwareFilter=$false}
        Camera=[ordered]@{GlobalDeviceSwitchImplemented=$true;PhysicalToggleTested=$false;PackagedUserConsentImplemented=$true;GroupPolicySupportedOnThisPC=$false;CaptureEnforcementTested=$false;ArbitraryClassicExeEnforcement=$false}
        DriverDeveloped=$true;DriverBuilt=$true;DriverInstalled=$false;KernelRuntimeTested=$false;Phase2FullyCompleted=$false
        Unresolved=@('EXE/write filter compiled but unsigned, not loaded and not kernel-tested','Camera interception for arbitrary classic EXE is not implemented','Kernel write filter not validated; not a completed behavioral ransomware engine','Native TXT read access denied; cause unconfirmed')
        DriverRelease=$driverCheck;DriverPolicyTests=$policyTests;InstalledSmokeVerified=$false
    }
    [IO.File]::WriteAllText((Join-Path $target 'VERIFICATION.json'),($verification|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    [pscustomobject]@{Installed=$target;Version='3.3.0';SHA256=$expected;Backup=$backup;Checks=$verification.AutomatedChecks}
}catch{
    # Возврат прежних файлов не затрагивает пользовательское хранилище Data.
    foreach($item in Get-ChildItem -LiteralPath $backup|Where-Object{$_.Name-ne'PC Guard.atomic-backup.exe'}){Copy-Item -LiteralPath $item.FullName -Destination $target -Recurse -Force}
    throw
}
