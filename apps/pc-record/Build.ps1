param([string]$Destination=(Join-Path $PSScriptRoot 'build'),[string]$UpdatesSource='')
$ErrorActionPreference='Stop'
$Destination=[IO.Path]::GetFullPath($Destination)
if([IO.Path]::GetPathRoot($Destination) -ine 'D:\'){throw 'Сборка разрешена только на D'}
# Не заменяем исполняемый файл работающей копии и не используем пути через ссылки.
for($dir=[IO.DirectoryInfo]::new($Destination);$dir;$dir=$dir.Parent){if($dir.Exists -and ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Папка сборки содержит ссылку'}}
foreach($process in (Get-Process -Name 'PC Record' -ErrorAction SilentlyContinue)){try{if([IO.Path]::GetDirectoryName($process.Path) -ieq $Destination){throw 'Закройте PC Record в папке сборки'}}catch [System.ComponentModel.Win32Exception]{throw 'Не удалось проверить путь работающей программы'}}
if(!$UpdatesSource){foreach($candidate in @((Join-Path $PSScriptRoot '..\..\updates'),(Join-Path $PSScriptRoot '..\CreativeRelab-Software\repo\updates'))){if(Test-Path -LiteralPath (Join-Path $candidate 'Build-Updates.ps1')){$UpdatesSource=$candidate;break}}}
if(!(Test-Path -LiteralPath (Join-Path $UpdatesSource 'Build-Updates.ps1'))){throw 'Укажите -UpdatesSource с исходниками общего центра обновлений'}
$helperDestination=Join-Path $Destination 'helper-build'
& (Join-Path $UpdatesSource 'Build-Updates.ps1') -Destination $helperDestination | Out-Null
[void][IO.Directory]::CreateDirectory($Destination)
foreach($name in @('public','tools','licenses','system')){[void][IO.Directory]::CreateDirectory((Join-Path $Destination $name))}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'public\index.html') -Destination (Join-Path $Destination 'public\index.html')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'public\assets') -Destination (Join-Path $Destination 'public') -Recurse -Force
$web=Join-Path $PSScriptRoot 'dependencies\microsoft.web.webview2.1.0.4314.35'
foreach($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')){Copy-Item -LiteralPath (Join-Path $web ('lib\net462\'+$name)) -Destination $Destination}
Copy-Item -LiteralPath (Join-Path $web 'build\native\x64\WebView2Loader.dll') -Destination $Destination
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dependencies\screenrecorderlib.7.0.1\build\x64\ScreenRecorderLib.dll') -Destination $Destination
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'dependencies\ffmpeg-release') -File | Where-Object {$_.Extension -in @('.exe','.dll') -or $_.Name -like '*LICENSE*' -or $_.Name -like 'COPYING*' -or $_.Name -eq 'GCC-RUNTIME-LIBRARY-EXCEPTION.txt' -or $_.Name -eq 'SOURCE.txt'} | ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Destination 'tools')}
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'licenses') -File | ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Destination 'licenses')}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'branding\app.ico') -Destination (Join-Path $Destination 'app.ico')
Copy-Item -LiteralPath (Join-Path $helperDestination 'Creative Relab Updates.exe') -Destination $Destination
$manifest='<?xml version="1.0" encoding="utf-8"?><assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1"><assemblyIdentity version="1.0.0.0" name="PCRecord"/><trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security></trustInfo><compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application></compatibility><application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings><dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness></windowsSettings></application></assembly>'
[IO.File]::WriteAllText((Join-Path $Destination 'app.manifest'),$manifest,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'system\ReadMedia.ps1') -Destination (Join-Path $Destination 'system\ReadMedia.ps1')
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$references=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll',(Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll'),(Join-Path $Destination 'ScreenRecorderLib.dll'),(Join-Path $Destination 'Microsoft.Web.WebView2.Core.dll'),(Join-Path $Destination 'Microsoft.Web.WebView2.WinForms.dll')) | ForEach-Object { '/reference:'+$_ }
$sources=Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'native') -File -Filter '*.cs' | ForEach-Object FullName
& $compiler /nologo /target:winexe /platform:x64 /langversion:5 /warn:4 /warnaserror+ /optimize+ ('/out:'+(Join-Path $Destination 'PC Record.exe')) ('/win32icon:'+(Join-Path $Destination 'app.ico')) ('/win32manifest:'+(Join-Path $Destination 'app.manifest')) $references $sources
if($LASTEXITCODE -ne 0){throw 'Не удалось собрать PC Record'}
foreach($doc in @('README.md','CHANGES.md','LICENSE.txt')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $doc) -Destination $Destination}
Get-FileHash -LiteralPath (Join-Path $Destination 'PC Record.exe') -Algorithm SHA256 | Select-Object Path,Hash