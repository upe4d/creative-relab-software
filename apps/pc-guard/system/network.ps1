# Аудит конфигурации только на чтение: пароли, cookies и содержимое переписки не читаются.
function Get-GuardNetwork {
    $issues = New-Object 'Collections.Generic.List[string]'; $dns = @(); $proxy = @(); $hosts = @(); $extensions = New-Object 'Collections.Generic.List[object]'
    try { $dns = @(Get-DnsClientServerAddress | Where-Object { $_.ServerAddresses.Count -gt 0 } | Sort-Object InterfaceIndex,AddressFamily | ForEach-Object { [pscustomobject]@{adapter=$_.InterfaceAlias;index=$_.InterfaceIndex;family=[string]$_.AddressFamily;addresses=(@($_.ServerAddresses)-join ', ')} }) }
    catch { $issues.Add('DNS: ' + $_.Exception.Message) }
    try {
        $settings = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
        $proxy = @([pscustomobject]@{scope='Пользователь Windows';enabled=[bool]$settings.ProxyEnable;server=[string]$settings.ProxyServer;pac=[string]$settings.AutoConfigURL})
    } catch { $issues.Add('Прокси: ' + $_.Exception.Message) }
    try {
        $path = Join-Path $env:WINDIR 'System32\drivers\etc\hosts'
        $file = Get-Item -LiteralPath $path
        if ($file.Length -gt 1048576) { throw 'hosts больше 1 МиБ: просмотр пропущен.' }
        $number=0
        $hosts = @(Get-Content -LiteralPath $path | ForEach-Object { $number++; $line=($_ -split '#',2)[0].Trim(); if($line){$parts=$line -split '\s+';[pscustomobject]@{line=$number;address=$parts[0];domains=($parts[1..($parts.Length-1)]-join ' ')}} })
    } catch { $issues.Add('hosts: ' + $_.Exception.Message) }
    $browsers = @(@{name='Chrome';root=(Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data')},@{name='Edge';root=(Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\User Data')},@{name='Яндекс';root=(Join-Path $env:LOCALAPPDATA 'Yandex\YandexBrowser\User Data')})
    foreach ($browser in $browsers) {
        if (-not (Test-Path -LiteralPath $browser.root)) { continue }
        $profiles = @(Get-ChildItem -LiteralPath $browser.root -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'Default' -or $_.Name -match '^Profile \d+$' } | Select-Object -First 30)
        foreach ($profile in $profiles) {
            $directory=Join-Path $profile.FullName 'Extensions'; if(-not(Test-Path -LiteralPath $directory)){continue}
            foreach($extension in @(Get-ChildItem -LiteralPath $directory -Directory -ErrorAction SilentlyContinue | Select-Object -First 300)) {
                try {
                    if(($extension.Attributes -band [IO.FileAttributes]::ReparsePoint)-ne 0){continue}
                    $versions=@(Get-ChildItem -LiteralPath $extension.FullName -Directory | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
                    if(-not $versions){continue};$manifestPath=Join-Path $versions[0].FullName 'manifest.json'
                    if(-not(Test-Path -LiteralPath $manifestPath)){continue};if((Get-Item -LiteralPath $manifestPath).Length-gt 1048576){continue}
                    $manifest=[IO.File]::ReadAllText($manifestPath,[Text.Encoding]::UTF8)|ConvertFrom-Json
                    $extensions.Add([pscustomobject]@{browser=$browser.name;profile=$profile.Name;id=$extension.Name;name=$manifest.name;version=$manifest.version;permissions=(@($manifest.permissions)-join ', ');state='Установлено; активность не проверена'})
                }catch{$issues.Add('Расширение '+$extension.Name+': '+$_.Exception.Message)}
            }
        }
    }
    return [pscustomobject]@{dns=@($dns);proxy=@($proxy);hosts=@($hosts);extensions=@($extensions.ToArray());issues=@($issues.ToArray());checked=(Get-Date).ToString('o')}
}
