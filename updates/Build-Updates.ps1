param([string]$Destination='D:\Codex\work\CreativeRelab-Software\builds\updates')
$ErrorActionPreference='Stop'
if([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Destination))-ine'D:\'){throw 'Сборка только на D'}
New-Item -ItemType Directory -Path $Destination -Force|Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319';$root=Split-Path -Parent $PSScriptRoot
$references=foreach($name in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Net.Http.dll')){
 $path=Join-Path $framework $name
 if(!(Test-Path -LiteralPath $path)){$gac=Join-Path $env:WINDIR ('Microsoft.NET\assembly\GAC_MSIL\'+[IO.Path]::GetFileNameWithoutExtension($name));$path=(Get-ChildItem -LiteralPath $gac -Filter $name -Recurse -File|Select-Object -First 1).FullName}
 if(!(Test-Path -LiteralPath $path)){throw ('Нет ссылки '+$name)}
 '/reference:'+$path
}
$exe=Join-Path $Destination 'Creative Relab Updates.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /langversion:5 /warn:4 /warnaserror+ /optimize+ ('/out:'+$exe) ('/win32icon:'+(Join-Path $root 'branding\creative-relab.ico')) ('/resource:'+(Join-Path $root 'branding\creative-relab-avatar.png')+',Relab.Avatar.png') $references (Join-Path $PSScriptRoot 'Program.cs') (Join-Path $PSScriptRoot 'ReleaseClient.cs') (Join-Path $PSScriptRoot 'UpdateForm.cs') (Join-Path $PSScriptRoot 'UpdateTests.cs') (Join-Path $PSScriptRoot 'RoundedButton.cs') (Join-Path $PSScriptRoot 'NativeChrome.cs')
if($LASTEXITCODE){throw 'Сборка центра обновлений не прошла'}
$p=Start-Process -FilePath $exe -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
if($p.ExitCode){throw 'Тесты центра обновлений не прошли'}
Get-FileHash -LiteralPath $exe -Algorithm SHA256
