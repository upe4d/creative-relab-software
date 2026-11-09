param([string]$Destination = $PSScriptRoot)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$argsList=@('/nologo','/target:winexe','/platform:x64','/langversion:5','/optimize+','/warn:4','/warnaserror+','/utf8output',('/out:'+ (Join-Path $Destination 'PC Drivers.exe')),('/win32icon:'+(Join-Path $PSScriptRoot 'native\app.ico')), '/reference:System.dll','/reference:System.Core.dll','/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll')
foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'public') -Recurse -File){$relative=$file.FullName.Substring((Join-Path $PSScriptRoot 'public').Length+1).Replace('\','.');$argsList+=('/resource:'+$file.FullName+',PCDrivers.'+$relative)}
$argsList+=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'native') -Filter '*.cs' -File | ForEach-Object FullName)
& $compiler @argsList
if($LASTEXITCODE -ne 0){throw 'PC Drivers: ошибка сборки'}
Get-FileHash -LiteralPath (Join-Path $Destination 'PC Drivers.exe') -Algorithm SHA256
