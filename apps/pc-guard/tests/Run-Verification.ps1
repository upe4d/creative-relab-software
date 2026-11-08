param([string]$Build='D:\Codex\work\PCGuard-v5-build',[switch]$BrowserOnly,[string]$ExpectedApplicationSHA='')
$ErrorActionPreference='Stop'
$source=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $build 'PC Guard.exe'
function Stop-TestHost {
    $path=Join-Path $build 'Data\test-runtime.json'
    if(-not(Test-Path -LiteralPath $path)){return}
    $r=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json;$process=Get-Process -Id $r.pid -ErrorAction SilentlyContinue
    if(-not$process){return}
    if($process.Path-ine$exe){throw 'PID тестового экземпляра относится к другой программе.'}
    $reply=Invoke-RestMethod -Uri ('http://127.0.0.1:'+$r.port+'/api') -Method Post -Headers @{'X-PCGuard-Token'=$r.token} -ContentType 'application/json' -Body '{"action":"shutdown"}' -TimeoutSec 10
    if(-not$reply.ok){throw 'Тестовый экземпляр не подтвердил остановку.'}
    if(-not$process.WaitForExit(15000)){throw 'Тестовый экземпляр не остановился.'}
}
function Native-Test([string]$argument){$p=Start-Process -FilePath $exe -ArgumentList $argument -WindowStyle Hidden -PassThru;$p.WaitForExit();if($p.ExitCode-ne0){throw ('Нативный тест завершился с ошибкой: '+$argument)}}
Stop-TestHost
if($BrowserOnly){if($ExpectedApplicationSHA-notmatch'^[0-9A-Fa-f]{64}$'-or(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash-ne$ExpectedApplicationSHA){throw 'Не подтверждён EXE для повторения браузерных проверок.'};$refused=$null}else{
& (Join-Path $source 'Build-Native.ps1') -Destination $build
Native-Test '--self-test';Native-Test '--suite-test';Native-Test '--tray-test'
$windowsPs=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
foreach($name in @('startup-tests.ps1','camera-policy-tests.ps1','camera-consent-tests.ps1','provider-tests.ps1')){
    & $windowsPs -NoProfile -ExecutionPolicy Bypass -File (Join-Path $build ('tests\'+$name)) | Out-Null
    if($LASTEXITCODE-ne0){throw ('Не пройден системный тест '+$name)}
}
$serviceBefore=Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\PCGuardFilter'
$refused=$false;try{& (Join-Path $build 'driver\Install-Driver.ps1') | Out-Null}catch{$refused=$true}
if(-not$refused-or(Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\PCGuardFilter')-ne$serviceBefore){throw 'Проверка отказа установки неподписанного драйвера не прошла.'}
}
$hostProcess=Start-Process -FilePath $exe -ArgumentList '--headless' -WindowStyle Hidden -PassThru
try{
    $ready=$false
    for($attempt=0;$attempt-lt100;$attempt++){
        $r=Get-Content -LiteralPath (Join-Path $build 'Data\test-runtime.json') -Raw -ErrorAction SilentlyContinue|ConvertFrom-Json
        if($r-and$r.pid-eq$hostProcess.Id){$ready=$true;break}
        if($hostProcess.HasExited){throw 'Тестовый сервер завершился при запуске.'}
        Start-Sleep -Milliseconds 200
    }
    if(-not$ready){throw 'Тестовый API не запущен.'}
    foreach($name in @('browser-tests.mjs','suite-browser-tests.mjs')){
        & 'C:\Program Files\nodejs\node.exe' (Join-Path $build ('tests\'+$name)) $build
        if($LASTEXITCODE-ne0){throw ('Не пройден тест интерфейса '+$name)}
    }
}finally{Stop-TestHost}
$results=[ordered]@{}
foreach($name in @('native-results.json','startup-results.json','suite-results.json','browser-results.json','suite-browser-results.json','camera-policy-results.json','camera-consent-results.json','provider-results.json')){
    $r=Get-Content -LiteralPath (Join-Path $build ('Data\Tests\'+$name)) -Raw|ConvertFrom-Json
    $results[$name]=$r.checks
}
$summary=[ordered]@{Date=(Get-Date).ToString('o');ApplicationSHA256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;Checks=$results;ApplicationChecks=($results.Values|Measure-Object -Sum).Sum;NativeChecksReused=$BrowserOnly.IsPresent;UnsignedDriverInstallRefused=$refused;CameraPhysicallyToggled=$false;DefenderSwitched=$false;IObitSwitched=$false;KernelRuntimeTested=$false}
[IO.File]::WriteAllText((Join-Path $build 'Data\Tests\verification-summary.json'),($summary|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$summary|ConvertTo-Json -Depth 5
