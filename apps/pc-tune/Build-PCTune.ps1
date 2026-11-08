param([string]$Destination='D:\PC Tune')
$ErrorActionPreference='Stop'
# Локальный SDK, NuGet и временные файлы не меняют системный PATH.
$tool='D:\Codex\work\PC-Tune-toolchain'
$env:DOTNET_ROOT=Join-Path $tool 'dotnet'
$env:DOTNET_CLI_HOME=Join-Path $tool 'cli'
$env:NUGET_PACKAGES=Join-Path $tool 'packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:TEMP='D:\Codex\tmp';$env:TMP=$env:TEMP
$dotnet=Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Destination))-ne'D:\'){throw 'Сборка разрешена только на D'}
if(!(Test-Path -LiteralPath $dotnet)){throw 'Нужен переносимый SDK .NET 10: путь указан в README-PC-TUNE-RU.md'}
& $dotnet build "$PSScriptRoot\optimizerDuck.slnx" -c Release --verbosity quiet
if($LASTEXITCODE){throw 'Сборка не прошла'}
# Выбраны тесты без применения системных настроек и команд отката.
& $dotnet "$PSScriptRoot\optimizerDuck.Test\bin\Release\net10.0-windows10.0.17763.0\optimizerDuck.Test.dll" -class '*LanguageManagerTests' -class '*RevertStepSerializationTests' -longRunning 30 -result-xml "$PSScriptRoot\optimizerDuck.Test\selected-results.xml"
if($LASTEXITCODE){throw 'Тесты не прошли'}
& $dotnet publish "$PSScriptRoot\optimizerDuck\optimizerDuck.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o $Destination --verbosity quiet
if($LASTEXITCODE){throw 'Публикация не прошла'}
Copy-Item -LiteralPath (Join-Path $Destination 'optimizerDuck.exe') -Destination (Join-Path $Destination 'PC Tune.exe')
foreach($name in @('LICENSE','THIRD-PARTY-NOTICES.md','UPSTREAM.txt','README-PC-TUNE-RU.md','CHANGES-PC-TUNE-RU.md')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $Destination -Force}
Write-Output (Join-Path $Destination 'PC Tune.exe')