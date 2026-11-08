$ErrorActionPreference = 'Stop'
$pcBase = Split-Path -Parent $PSScriptRoot
[void][Reflection.Assembly]::LoadFrom((Join-Path $pcBase 'PC Care.exe'))
$pcStore = New-Object PCCare.AppStore (Join-Path $pcBase 'Data\Verification')
$pcCleaner = New-Object PCCare.CleanerEngine ($pcStore.Settings, [PCCare.CleanerEngine]::DefaultRoots())
$pcResult = $pcCleaner.Scan([Threading.CancellationToken]::None, $true)
$pcStartup = [PCCare.StartupManager]::Default($pcStore).List()
$pcMonitor = New-Object PCCare.SystemMonitor
[void]$pcMonitor.Read()
Start-Sleep -Milliseconds 250
$pcSample = $pcMonitor.Read()
$pcLargeSettings = New-Object PCCare.Settings
$pcLargeSettings.LargeFileMinimumMB = 1
$pcLarge = [PCCare.LargeFiles]::Scan($pcBase, $pcLargeSettings, [Threading.CancellationToken]::None)
$pcReport = [ordered]@{
    timestamp = [DateTimeOffset]::Now.ToString('o')
    mode = 'Read-only verification; no real files deleted, no startup changes, no recycle-bin emptying'
    cleanup = [ordered]@{
        categories = @($pcResult.Categories | Select-Object Id,Name,Bytes,Count,Note)
        scanned_files = $pcResult.Stats.Files
        skipped = $pcResult.Stats.Skipped
        limited = $pcResult.Stats.Limited
        notes = @($pcResult.Notes)
    }
    startup = [ordered]@{ entries = $pcStartup.Entries.Count; errors = @($pcStartup.Errors) }
    monitor = [ordered]@{ cpu = $pcSample.CpuPercent; cpu_available = $pcSample.CpuAvailable; memory_available = $pcSample.MemoryAvailable; total_ram = $pcSample.TotalMemory; used_ram = $pcSample.UsedMemory; disks = @($pcSample.Disks); errors = @($pcSample.Errors) }
    large_files = [ordered]@{ root = $pcBase; scanned_files = $pcLarge.Stats.Files; matched = $pcLarge.Matched; results = $pcLarge.Files.Count }
}
$pcReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $pcBase 'Data\Verification\read-only-results.json') -Encoding UTF8
if (-not $pcSample.MemoryAvailable -or -not $pcSample.CpuAvailable -or $pcSample.Disks.Count -eq 0) { throw 'Live monitor unavailable' }
if ($pcStartup.Errors.Count -gt 0) { Write-Warning ($pcStartup.Errors -join '; ') }
Write-Output 'Read-only verification completed.'
