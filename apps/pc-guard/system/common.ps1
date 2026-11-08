# Общие операции Windows. Запросы ограничены известными действиями приложения.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -le 5) {
    # При запуске из PowerShell 7 не наследуем несовместимые модули его окружения.
    $env:PSModulePath = (Join-Path $PSHOME 'Modules') + ';' + (Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules')
}
$GuardRoot = Split-Path -Parent $PSScriptRoot
$GuardData = Join-Path $GuardRoot 'Data'
$GuardUtf8 = New-Object Text.UTF8Encoding($false)

function Write-GuardJson([string]$Path, $Value) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject $Value -Depth 25 -Compress), $GuardUtf8)
    # Короткая блокировка читателем не должна терять финальный результат операции.
    for($attempt=0;;$attempt++) {
        try {
            if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, ($Path + '.bak')) }
            else { [IO.File]::Move($temporary, $Path) }
            return
        } catch {
            $code=$_.Exception.GetBaseException().HResult -band 0xffff
            if($attempt-ge6-or$code-notin@(32,33)){throw}
            Start-Sleep -Milliseconds (10 -shl $attempt)
        }
    }
}

function Get-GuardJson([string]$Path, $Fallback) {
    if (-not [IO.File]::Exists($Path)) { return $Fallback }
    return ([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json)
}

function Get-GuardId([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))).Replace('-', '').ToLowerInvariant()) }
    finally { $sha.Dispose() }
}

function Assert-LocalPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Length -gt 2000 -or $Path -notmatch '^[A-Za-z]:[\\/]' -or $Path -match '[\x00-\x1F]') { throw 'Нужен полный локальный путь.' }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full)) { throw 'Файл или папка не найдены.' }
    return $full
}

function Get-TargetPath([string]$Command) {
    if (-not $Command) { return $null }
    $value = [Environment]::ExpandEnvironmentVariables($Command.Trim())
    if ($value.StartsWith('\SystemRoot\', [StringComparison]::OrdinalIgnoreCase)) { $value = $env:WINDIR + $value.Substring(11) }
    if ($value -match '^"([^"]+)"') { $candidate = $Matches[1] }
    elseif ($value -match '^(.+?\.(exe|com|bat|cmd))(?=\s|$)') { $candidate = $Matches[1] }
    else { $candidate = ($value -split '\s+', 2)[0] }
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [IO.Path]::GetFullPath($candidate) }
    if ($candidate -notmatch '[\\/]') {
        $found = Get-Command -Name $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { return $found.Source }
    }
    return $null
}

function Get-FileBrief([string]$Path, [bool]$WithHash = $false) {
    if (-not $Path -or -not [IO.File]::Exists($Path)) { return [pscustomobject]@{path=$Path;size=$null;signature='Файл не найден';publisher='';sha256=$null;signatureStatus='Missing'} }
    $file = Get-Item -LiteralPath $Path -Force
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $publisher = ''; if ($signature.SignerCertificate) { $publisher = $signature.SignerCertificate.Subject }
    $hash = $null; if ($WithHash) { $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
    $signatureText = switch ($signature.Status.ToString()) { 'Valid' {'Действительна'} 'NotSigned' {'Без подписи'} 'HashMismatch' {'Не совпадает хеш'} 'NotTrusted' {'Нет доверия'} 'UnknownError' {'Не удалось проверить для этого формата'} default {'Не определена: '+$signature.Status.ToString()} }
    return [pscustomobject]@{path=$file.FullName;size=$file.Length;signature=$signatureText;signatureStatus=$signature.Status.ToString();publisher=$publisher;sha256=$hash;modified=$file.LastWriteTimeUtc.ToString('o')}
}

function Get-GuardFileAnalysis($Arguments) {
    $path = Assert-LocalPath ([string]$Arguments.path)
    if (-not [IO.File]::Exists($path)) { throw 'Для анализа выбери файл.' }
    $before = Get-Item -LiteralPath $path -Force
    $brief = Get-FileBrief $path $true
    $after = Get-Item -LiteralPath $path -Force
    if ($before.Length -ne $after.Length -or $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc) { throw 'Файл изменился во время анализа. Повтори проверку.' }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    return [pscustomobject]@{file=$brief;version=$version.FileVersion;product=$version.ProductName;company=$version.CompanyName;note='Подпись и хеш не являются антивирусным вердиктом.'}
}

function Select-GuardPath($Arguments) {
    Add-Type -AssemblyName System.Windows.Forms
    if ($Arguments.kind -eq 'folder') {
        $dialog = New-Object Windows.Forms.FolderBrowserDialog
        $dialog.Description = 'PC Guard: выбери папку или USB-диск'; $dialog.ShowNewFolderButton = $false
        try { if ($dialog.ShowDialog() -eq [Windows.Forms.DialogResult]::OK) { return @{path=$dialog.SelectedPath;cancelled=$false} } }
        finally { $dialog.Dispose() }
    } else {
        $dialog = New-Object Windows.Forms.OpenFileDialog; $dialog.Title = 'PC Guard: выбери файл'; $dialog.CheckFileExists = $true
        try { if ($dialog.ShowDialog() -eq [Windows.Forms.DialogResult]::OK) { return @{path=$dialog.FileName;cancelled=$false} } }
        finally { $dialog.Dispose() }
    }
    return @{path=$null;cancelled=$true}
}

function Open-GuardWindow($Arguments) {
    switch ($Arguments.target) {
        'security' { Start-Process 'windowsdefender://threat' }
        'history' { Start-Process 'windowsdefender://history' }
        'camera' { Start-Process 'ms-settings:privacy-webcam' }
        'services' { Start-Process -FilePath (Join-Path $env:WINDIR 'System32\mmc.exe') -ArgumentList (Join-Path $env:WINDIR 'System32\services.msc') }
        'tasks' { Start-Process -FilePath (Join-Path $env:WINDIR 'System32\mmc.exe') -ArgumentList (Join-Path $env:WINDIR 'System32\taskschd.msc') }
        'data' { Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('"' + $GuardData + '"') }
        'explorer' { $path = Assert-LocalPath ([string]$Arguments.path); Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('/select,"' + $path.Replace('"','') + '"') }
        default { throw 'Неизвестное системное окно.' }
    }
    return @{opened=$Arguments.target}
}
