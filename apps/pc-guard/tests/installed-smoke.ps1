$ErrorActionPreference='Stop'
$root='D:\PC Guard'
# Проверяем установленный экземпляр, не переключая антивирусы, камеру и автозагрузку.
$process=Start-Process -FilePath (Join-Path $root 'PC Guard.exe') -ArgumentList '--agent' -WindowStyle Hidden -PassThru
$runtime=$null
for($attempt=0;$attempt-lt100;$attempt++){
    try{$candidate=Get-Content -LiteralPath (Join-Path $root 'Data\runtime.json') -Raw|ConvertFrom-Json;if($candidate.pid-eq$process.Id){$runtime=$candidate;break}}catch{}
    if($process.HasExited){throw 'Установленный PC Guard завершился при запуске.'}
    Start-Sleep -Milliseconds 200
}
if(-not$runtime){throw 'Нет актуального файла запуска.'}
function Invoke-LocalApi($action,$arguments=@{}){
    $arguments.action=$action
    $reply=Invoke-RestMethod -Uri ('http://127.0.0.1:'+$runtime.port+'/api') -Method Post -ContentType 'application/json' -Headers @{'X-PCGuard-Token'=$runtime.token} -Body ($arguments|ConvertTo-Json -Depth 5) -TimeoutSec 15
    if(-not$reply.ok){throw $reply.error}
    return $reply.data
}
function Wait-LocalJob($action){
    $initial=Invoke-LocalApi $action
    if(-not$initial.job){return $initial}
    for($attempt=0;$attempt-lt150;$attempt++){
        $job=Invoke-LocalApi 'job' @{id=$initial.job}
        if($job.state-eq'done'){return $job.data}
        if($job.state-eq'error'){throw $job.message}
        Start-Sleep -Milliseconds 300
    }
    throw ('Истекло ожидание '+$action)
}
$meta=Invoke-LocalApi 'meta';$suite=Invoke-LocalApi 'suite_status'
if($meta.version-ne'3.3.0'-or$meta.root-ine$root){throw 'Неверная версия или каталог API.'}
if($suite.engine.hashes-ne19788-or-not$suite.realtime.enabled-or$suite.realtime.roots.Count-ne1-or$suite.realtime.roots[0]-ine(Join-Path $root 'Incoming')){throw 'Не совпала начальная конфигурация собственного движка.'}
if($suite.driver.installed-or$suite.driver.preExecutionBlocking-or$suite.driver.perAppCameraBlocking){throw 'Состояние перехвата некорректно.'}
$status=Wait-LocalJob 'status';$camera=Wait-LocalJob 'camera';$cameraUser=Wait-LocalJob 'camera_apps'
if($cameraUser.mode-ne'current-user-consent'-or$cameraUser.requiresAdmin){throw 'Не включён пользовательский режим камеры для Home.'}
if($status.defender.mode-ne'Passive Mode'){throw 'Режим Defender отличается от проверенного ранее; требуется сверка.'}
if(-not@($status.products|Where-Object{$_.name-match'IObit'}).Count){throw 'Не получена регистрация IObit в Центре безопасности.'}
$reportPath=Join-Path $root 'VERIFICATION.json'
$report=Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json
$report.InstalledSmokeVerified=$true
$report|Add-Member -NotePropertyName InstalledSmoke -NotePropertyValue ([ordered]@{
    Date=(Get-Date).ToString('o');Version=$meta.version;Root=$meta.root;DefenderMode=$status.defender.mode;DefenderRealtime=$status.defender.realtime
    WindowsEdition=(Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID;IObitRegistered=$true;CameraMode=$cameraUser.mode;CameraPackagesRead=@($cameraUser.rows).Count;CameraDevicesRead=@($camera.devices).Count;CameraToggled=$false;OwnAutostart=$suite.autostart
    RealtimeRoots=$suite.realtime.roots;InitialRules=$suite.engine.hashes;OwnDriverInstalled=$false;RuntimePid=$runtime.pid
}) -Force
[IO.File]::WriteAllText($reportPath,($report|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
# Повторный обычный запуск открывает окно уже работающего агента с тем же ключом.
Start-Process -FilePath (Join-Path $root 'PC Guard.exe') -WindowStyle Hidden
[pscustomobject]@{Version=$meta.version;Root=$meta.root;DefenderMode=$status.defender.mode;IObitRegistered=$true;CameraDevices=@($camera.devices).Count;InitialRules=$suite.engine.hashes;Realtime=$suite.realtime.enabled;Verified=$true}
