param([Parameter(Mandatory=$true)][string]$Path,[long]$Offset=0,[long]$Count=-1)
$ErrorActionPreference='Stop'
try {
 $root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
 $file=[IO.Path]::GetFullPath($Path)
 if([IO.Path]::GetPathRoot($root) -ine 'D:\'){throw 'Приложение должно находиться на D'}
 $allowed=@((Join-Path $root 'Recordings'),(Join-Path $root 'Data\Preview'),(Join-Path $root 'Data\Temp'))
 if(-not @($allowed|Where-Object {$file.StartsWith($_+'\',[StringComparison]::OrdinalIgnoreCase)}).Count){throw 'Чтение разрешено только для собственных медиафайлов'}
 for($node=New-Object IO.FileInfo($file);$null -ne $node; $node=if($node -is [IO.FileInfo]){$node.Directory}else{$node.Parent}){if(($node.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Ссылки не разрешены'}}
 $stream=[IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try {if($Offset -lt 0 -or $Offset -gt $stream.Length){throw 'Неверное смещение'};[void]$stream.Seek($Offset,[IO.SeekOrigin]::Begin);$output=[Console]::OpenStandardOutput();$remaining=if($Count -lt 0){$stream.Length-$Offset}else{[Math]::Min($Count,$stream.Length-$Offset)};$buffer=New-Object byte[] 65536;while($remaining -gt 0){$n=$stream.Read($buffer,0,[int][Math]::Min($buffer.Length,$remaining));if($n -le 0){break};$output.Write($buffer,0,$n);$remaining-=$n}} finally {$stream.Dispose()}
 exit 0
} catch {[Console]::Error.WriteLine($_.Exception.Message);exit 1}