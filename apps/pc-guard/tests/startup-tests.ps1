$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'system\common.ps1')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'system\startup.ps1')
$testRoot=Join-Path $GuardData ('Tests\'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$GuardData=$testRoot
$script:GuardTestStartupFolder=Join-Path $testRoot 'Startup';[void][IO.Directory]::CreateDirectory($script:GuardTestStartupFolder)
[void][IO.Directory]::CreateDirectory((Join-Path $testRoot 'Backups'))
$script:GuardTestRegistry='Software\PCGuard\Tests\'+[Guid]::NewGuid().ToString('N')
$checks=New-Object 'Collections.Generic.List[string]'
function Check([bool]$Condition,[string]$Name){if(-not$Condition){throw ('Проверка не пройдена: '+$Name)};$checks.Add($Name)}
function Must-Fail([scriptblock]$Code,[string]$Name){$failed=$false;try{&$Code}catch{$failed=$true};Check $failed $Name}
$base=Open-GuardRegistry 'HKCU' 64;$key=$base.CreateSubKey($script:GuardTestRegistry)
try{
    $original='%PCGUARD_TEST_VALUE%\test.exe --fixture'
    $key.SetValue('Guard Fixture',$original,[Microsoft.Win32.RegistryValueKind]::ExpandString)
    $snapshot=Get-GuardStartup;$entry=@($snapshot.rows|Where-Object{$_.name-eq'Guard Fixture'})[0]
    Check ($entry.command-ceq$original) 'Не раскрываются переменные в исходном значении'
    [void](Disable-GuardStartup ([pscustomobject]@{entry=$entry}))
    Check (-not($key.GetValueNames()-contains'Guard Fixture')) 'Тестовая запись отключена'
    $saved=@(Get-GuardBackups)[0];Check ($saved.command-ceq$original-and$saved.valueKind-eq2-and$saved.state-eq'Disabled') 'Резервная копия сохраняет текст и тип'
    [void](Restore-GuardStartup ([pscustomobject]@{entry=$entry}))
    Check ($key.GetValue('Guard Fixture',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)-ceq$original) 'Исходный текст восстановлен'
    Check ([int]$key.GetValueKind('Guard Fixture')-eq2) 'Тип ExpandString восстановлен'
    [void](Disable-GuardStartup ([pscustomobject]@{entry=$entry}));$key.SetValue('Guard Fixture','changed',[Microsoft.Win32.RegistryValueKind]::String)
    Must-Fail {Restore-GuardStartup ([pscustomobject]@{entry=$entry})} 'Конфликтующее значение не перезаписывается'
    Check ($key.GetValue('Guard Fixture')-ceq'changed') 'Конфликт сохранён без изменений'
    $pendingEntry=@((Get-GuardStartup).rows|Where-Object{$_.name-eq'Guard Fixture'})[0]
    Check ([bool]$pendingEntry.hasBackup) 'Возврат доступен при появлении исходной записи'
    $key.DeleteValue('Guard Fixture');[void](Restore-GuardStartup ([pscustomobject]@{entry=$entry}))
    $key.SetValue('IObit Fixture','D:\IObit\IMF.exe',[Microsoft.Win32.RegistryValueKind]::String)
    $protected=@((Get-GuardStartup).rows|Where-Object{$_.name-eq'IObit Fixture'})[0]
    Must-Fail {Disable-GuardStartup ([pscustomobject]@{entry=$protected})} 'Изменение IObit заблокировано'
    Check ($key.GetValueNames()-contains'IObit Fixture') 'Запись IObit осталась на месте'
    $file=Join-Path $script:GuardTestStartupFolder 'fixture.cmd';[IO.File]::WriteAllText($file,'@echo PC Guard fixture',[Text.Encoding]::ASCII)
    $fileEntry=@((Get-GuardStartup).rows|Where-Object{$_.originalPath-eq$file})[0]
    [void](Disable-GuardStartup ([pscustomobject]@{entry=$fileEntry}))
    Check (-not[IO.File]::Exists($file)) 'Тестовый файл Startup отключён'
    $fileBackup=@(Get-GuardBackups|Where-Object{$_.id-eq$fileEntry.id})[0]
    Check ([IO.File]::Exists($fileBackup.savedPath)) 'Копия файла существует'
    [void](Restore-GuardStartup ([pscustomobject]@{entry=$fileEntry}))
    Check ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash-eq$fileBackup.hash) 'Файл возвращён с исходным SHA-256'
    [void](Disable-GuardStartup ([pscustomobject]@{entry=$fileEntry}));[IO.File]::WriteAllText($file,'other')
    Must-Fail {Restore-GuardStartup ([pscustomobject]@{entry=$fileEntry})} 'Другой файл Startup не перезаписывается'
    Check ([IO.File]::ReadAllText($file)-ceq'other') 'Конфликтующий файл сохранён'
    $outside=[pscustomobject]@{kind='folder';originalPath=(Join-Path $testRoot 'outside.cmd');id=(Get-GuardId (Join-Path $testRoot 'outside.cmd'));name='Outside';command='test'}
    Must-Fail {Assert-GuardEntry $outside} 'Путь за пределами Startup отклонён'
    $invalid=[pscustomobject]@{kind='registry';hive='HKCU';view=64;key='Software\Other';name='x';id=('0'*64);valueKind=1;command='test'}
    Must-Fail {Assert-GuardEntry $invalid} 'Произвольный раздел реестра отклонён'
    $report=[pscustomobject]@{checks=$checks.Count;results=$checks.ToArray();realStartupModified=$false;fixture=$testRoot};Write-GuardJson (Join-Path (Split-Path -Parent $GuardData) 'startup-results.json') $report;$report|ConvertTo-Json -Depth 5
}finally{
    $key.Dispose();$base.Dispose()
    # Удаляется только созданный этим тестом ключ с заранее проверенным именем.
    if($script:GuardTestRegistry-match'^Software\\PCGuard\\Tests\\[a-f0-9]{32}$'){$cleanupBase=Open-GuardRegistry 'HKCU' 64;try{$cleanupBase.DeleteSubKeyTree($script:GuardTestRegistry,$false)}finally{$cleanupBase.Dispose()}}
}
