param([string]$Destination = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath $framework)) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$compiler = Join-Path $framework 'csc.exe'
$references = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','UIAutomationProvider.dll','UIAutomationTypes.dll')
$compilerReferences = foreach ($name in $references) {
    $reference = Join-Path $framework $name
    if (-not (Test-Path -LiteralPath $reference)) { $reference = Join-Path "$framework\WPF" $name }
    if (-not (Test-Path -LiteralPath $reference)) {
        $gacName = [IO.Path]::GetFileNameWithoutExtension($name)
        $gacRoots = @((Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_MSIL\$gacName"),(Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_64\$gacName"))
        $gacCandidates = foreach ($gacRoot in $gacRoots) { if (Test-Path -LiteralPath $gacRoot) { Get-ChildItem -LiteralPath $gacRoot -Filter $name -File -Recurse } }
        $reference = ($gacCandidates | Select-Object -First 1).FullName
    }
    if (-not $reference -or -not (Test-Path -LiteralPath $reference)) { throw "Reference unavailable: $name" }
    '/reference:' + $reference
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$output = Join-Path $Destination 'PC Care.exe'
& (Join-Path $PSScriptRoot 'MakeIcon.ps1') -Destination $PSScriptRoot
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/langversion:5','/optimize+','/warn:4','/warnaserror+','/utf8output',"/out:$output",("/win32manifest:" + (Join-Path $PSScriptRoot 'app.manifest')),("/resource:" + (Join-Path $PSScriptRoot 'MainWindow.xaml') + ',PCCare.MainWindow.xaml')) + $compilerReferences + @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | ForEach-Object FullName)
$arguments += @(("/win32icon:" + (Join-Path $PSScriptRoot 'app.ico')),("/resource:" + (Join-Path $PSScriptRoot 'app-icon.png') + ',PCCare.AppIcon.png'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Get-Item -LiteralPath $output | Select-Object FullName,Length
Get-FileHash -LiteralPath $output -Algorithm SHA256

# Центр обновлений собирается отдельно от кода приложения, без системной установки.
$updateBuildCandidates=@((Join-Path $PSScriptRoot '..\..\..\updates\Build-Updates.ps1'),(Join-Path $PSScriptRoot '..\updates\Build-Updates.ps1'))
foreach($updateBuild in $updateBuildCandidates){if(Test-Path -LiteralPath $updateBuild){& $updateBuild -Destination $Destination;break}}
