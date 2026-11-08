param([string]$Destination='D:\Codex\work\PCGuard-v5-build')
$ErrorActionPreference='Stop'
$node=Get-Command node.exe -ErrorAction SilentlyContinue
if($node){foreach($script in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'public\assets') -Filter '*.js' -File)){& $node.Source --check $script.FullName;if($LASTEXITCODE-ne0){throw ('Ошибка JavaScript: '+$script.Name)}}}
if([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Destination))-ine'D:\'){throw 'Сборка допускается только на D.'}
[void][IO.Directory]::CreateDirectory($Destination)
foreach($name in @('public','system','native','rules','driver','README.txt','ROADMAP.md','THIRD-PARTY.txt','Build-Native.ps1','Install-Update.ps1','Build-Indicators.py')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $Destination -Recurse -Force}
$tests=Join-Path $Destination 'tests';[void][IO.Directory]::CreateDirectory($tests)
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -File|Where-Object{$_.Extension-in@('.ps1','.mjs')}|ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination $tests -Force}
foreach($file in @(Get-ChildItem -LiteralPath $Destination -Filter '*.ps1' -Recurse -File|Where-Object{$_.FullName-notlike'*\Data\*'})){$text=[IO.File]::ReadAllText($file.FullName,[Text.Encoding]::UTF8);[IO.File]::WriteAllText($file.FullName,$text,(New-Object Text.UTF8Encoding($true)))}
Add-Type -AssemblyName System.Drawing
$bitmap=New-Object Drawing.Bitmap(256,256);$graphics=[Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias;$graphics.Clear([Drawing.Color]::FromArgb(36,54,80))
$font=New-Object Drawing.Font('Segoe UI',155,[Drawing.FontStyle]::Bold,[Drawing.GraphicsUnit]::Pixel)
$brush=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(146,181,255));$graphics.DrawString('G',$font,$brush,30,20)
$memory=New-Object IO.MemoryStream;$bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png);$png=$memory.ToArray()
$iconPath=Join-Path $Destination 'app.ico';$icon=[IO.File]::Create($iconPath);$writer=New-Object IO.BinaryWriter($icon)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]1);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$png.Length);$writer.Write([uint32]22);$writer.Write($png);$writer.Dispose();$memory.Dispose();$brush.Dispose();$font.Dispose();$graphics.Dispose();$bitmap.Dispose()
$manifest='<?xml version="1.0" encoding="utf-8"?><assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1"><assemblyIdentity version="3.3.0.0" name="PCGuard"/><trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security></trustInfo><compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application></compatibility></assembly>'
[IO.File]::WriteAllText((Join-Path $Destination 'app.manifest'),$manifest,[Text.Encoding]::UTF8)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $Destination 'native') -Filter '*.cs' -File|ForEach-Object FullName)
$resources=@(Get-ChildItem -LiteralPath (Join-Path $Destination 'public') -Recurse -File|Where-Object{$_.Extension-in@('.html','.css','.js','.svg','.png')}|ForEach-Object{$relative=$_.FullName.Substring((Join-Path $Destination 'public').Length+1).Replace('\','.');'/resource:'+ $_.FullName+',PCGuard.'+$relative})
& $compiler '/nologo' '/target:winexe' '/langversion:5' '/warn:4' '/warnaserror+' '/optimize+' '/reference:System.dll' '/reference:System.Core.dll' '/reference:System.Drawing.dll' '/reference:System.Security.dll' '/reference:System.Web.Extensions.dll' '/reference:System.Windows.Forms.dll' ('/win32icon:'+$iconPath) ('/win32manifest:'+(Join-Path $Destination 'app.manifest')) ('/out:'+(Join-Path $Destination 'PC Guard.exe')) $sources $resources ('/resource:'+(Join-Path $Destination 'rules\engine-seed.json')+',PCGuard.engine-seed.json')
if($LASTEXITCODE-ne0){throw 'Сборка C# завершилась с ошибкой.'}
Get-FileHash -LiteralPath (Join-Path $Destination 'PC Guard.exe') -Algorithm SHA256
