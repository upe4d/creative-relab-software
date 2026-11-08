param([Parameter(Mandatory=$true)][string]$PackageZip,[Parameter(Mandatory=$true)][string]$ProductJson,[Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference='Stop'
foreach($path in @($PackageZip,$ProductJson,$Output)){if([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($path))-ine'D:\'){throw 'Используйте диск D'}}
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')
$refs=foreach($name in $references){$path=Join-Path $framework $name;if(!(Test-Path -LiteralPath $path)){$base=[IO.Path]::GetFileNameWithoutExtension($name);$path=(Get-ChildItem -LiteralPath (Join-Path $env:WINDIR ('Microsoft.NET\assembly\GAC_MSIL\'+$base)) -Filter $name -Recurse -File|Select-Object -First 1).FullName};if(!(Test-Path -LiteralPath $path)){throw ('Нет ссылки: '+$name)};'/reference:'+$path}
& (Join-Path $framework 'csc.exe') '/nologo' '/target:winexe' '/platform:anycpu' '/langversion:5' '/warn:4' '/warnaserror+' '/optimize+' ('/out:'+$Output) ('/win32icon:'+(Join-Path $PSScriptRoot 'creative-relab.ico')) ('/resource:'+(Join-Path $PSScriptRoot 'creative-relab-avatar.png')+',Relab.Avatar.png') ('/resource:'+$PackageZip+',Relab.Package.zip') ('/resource:'+$ProductJson+',Relab.Product.json') $refs (Join-Path $PSScriptRoot 'Program.cs') (Join-Path $PSScriptRoot 'Package.cs') (Join-Path $PSScriptRoot 'PortableForm.cs')
if($LASTEXITCODE){throw 'Распаковщик не собран'}
$verify=Start-Process -FilePath $Output -ArgumentList '--verify-package' -WindowStyle Hidden -Wait -PassThru
if($verify.ExitCode){throw 'Пакет не прошёл проверку'}
$test=Start-Process -FilePath $Output -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
if($test.ExitCode){throw 'Распаковщик не прошёл проверку извлечения'}
Get-FileHash -LiteralPath $Output -Algorithm SHA256