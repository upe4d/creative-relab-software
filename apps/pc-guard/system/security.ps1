# Состояние и проверки Defender. Переключение защитных движков здесь не выполняется.
function Get-GuardProviderLabel($Product){
    $raw=[string]$Product.displayName;$path=[Environment]::ExpandEnvironmentVariables([string]$Product.pathToSignedProductExe).Trim('"')
    $damaged=[string]::IsNullOrWhiteSpace($raw)-or$raw-match'\A[?\uFFFD\s]+\z';$name=$raw
    if($damaged){
        if($path-match'(?i)\\HONOR\\PCManager\\qspublic\\QAXDefenderHelper\.exe\z'){$name='HONOR PC Manager (QAXDefenderHelper)'}
        elseif($path-match'(?i)\A[A-Z]:\\.+\.exe\z'){$name=[IO.Path]::GetFileNameWithoutExtension($path)}
        else{$name='Антивирус с повреждённым именем Windows'}
    }
    return [pscustomobject]@{name=$name;registeredName=$raw;nameRecovered=$damaged;productPath=$path;state=$Product.productState}
}
function Get-GuardSecurity {
    $issues = New-Object 'Collections.Generic.List[string]'
    $defender = $null; $products = @(); $firewall = @()
    try {
        $status = Get-MpComputerStatus
        $lastScan = $null; if ($status.QuickScanEndTime -and $status.QuickScanEndTime.Year -gt 2000) { $lastScan = $status.QuickScanEndTime.ToString('o') }
        $definitionDate = $null; if ($status.AntivirusSignatureLastUpdated) { $definitionDate = $status.AntivirusSignatureLastUpdated.ToString('o') }
        $defender = [pscustomobject]@{mode=$status.AMRunningMode;service=$status.AMServiceEnabled;antivirus=$status.AntivirusEnabled;realtime=$status.RealTimeProtectionEnabled;signatures=$status.AntivirusSignatureVersion;signaturesDate=$definitionDate;quickScanEnd=$lastScan;canScan=($status.AMRunningMode -eq 'Normal' -and $status.AMServiceEnabled -and $status.AntivirusEnabled)}
    } catch { $issues.Add('Defender: ' + $_.Exception.Message) }
    try { $products = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct | ForEach-Object { Get-GuardProviderLabel $_ }) }
    catch { $issues.Add('Центр безопасности: ' + $_.Exception.Message) }
    try { $firewall = @(Get-NetFirewallProfile | ForEach-Object { [pscustomobject]@{name=$_.Name;enabled=[bool]$_.Enabled} }) }
    catch { $issues.Add('Брандмауэр: ' + $_.Exception.Message) }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return [pscustomobject]@{checked=(Get-Date).ToString('o');defender=$defender;products=$products;firewall=$firewall;admin=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);issues=@($issues.ToArray())}
}

function Get-GuardThreats {
    $threatMap = @{}; $issues = New-Object 'Collections.Generic.List[string]'
    try { foreach ($threat in @(Get-MpThreat)) { $threatMap[[string]$threat.ThreatID] = $threat } }
    catch { $issues.Add('Справочник угроз: ' + $_.Exception.Message) }
    $records = @(Get-MpThreatDetection | Sort-Object InitialDetectionTime -Descending | Select-Object -First 2000)
    $rows = foreach ($record in $records) {
        $definition = $threatMap[[string]$record.ThreatID]
        $name = 'Угроза ' + $record.ThreatID; $level = 'Не определён'
        if ($definition) { $name = $definition.ThreatName; $level = switch ([int]$definition.SeverityID) { 1 {'Низкий'} 2 {'Средний'} 4 {'Высокий'} 5 {'Критический'} default {'Не определён'} } }
        $date = $null; if ($record.InitialDetectionTime) { $date = $record.InitialDetectionTime.ToString('o') }
        [pscustomobject]@{id=[string]$record.DetectionID;name=$name;level=$level;resources=(@($record.Resources)-join '; ');date=$date;status=$(if($record.ActionSuccess){'Действие выполнено'}else{'Проверь журнал Windows'});actionSuccess=$record.ActionSuccess;source='Microsoft Defender';process=$record.ProcessName}
    }
    return [pscustomobject]@{rows=@($rows);issues=@($issues.ToArray());note='Успех действия не всегда означает карантин. Точные действия и восстановление доступны в журнале Windows.'}
}

function Invoke-GuardScan($Arguments) {
    $status = Get-MpComputerStatus
    if ($status.AMRunningMode -ne 'Normal' -or -not $status.AMServiceEnabled -or -not $status.AntivirusEnabled) {
        throw ('Проверка не запускалась: Defender сейчас в режиме «' + $status.AMRunningMode + '». PC Guard не переключает его автоматически. Открой безопасность Windows для проверки доступности.')
    }
    if ($Arguments.type -notin @('QuickScan','FullScan','CustomScan')) { throw 'Недопустимый тип проверки.' }
    Set-GuardProgress 'Проверка средствами Microsoft Defender. Процент движок не сообщает.'
    $started = Get-Date
    if ($Arguments.type -eq 'CustomScan') { $scanPath = Assert-LocalPath ([string]$Arguments.path); Start-MpScan -ScanType CustomScan -ScanPath $scanPath }
    else { Start-MpScan -ScanType ([string]$Arguments.type) }
    return [pscustomobject]@{started=$started.ToString('o');finished=(Get-Date).ToString('o');type=$Arguments.type;path=$Arguments.path;note='Команда проверки завершена. Вердикты и действия смотри в разделе «Находки» и журнале Windows.'}
}

function Update-GuardSignatures {
    $status = Get-MpComputerStatus
    if ($status.AMRunningMode -ne 'Normal') { throw 'Обновление не запускалось: для этой команды PC Guard требуется активный Defender. Настройки защиты не изменены.' }
    Set-GuardProgress 'Обновление баз Microsoft Defender'
    Update-MpSignature
    return @{version=(Get-MpComputerStatus).AntivirusSignatureVersion}
}
