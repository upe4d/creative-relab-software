$ErrorActionPreference='Stop'
# Зависимости нужны для сборки, их загрузка не устанавливает компоненты в Windows.
if([IO.Path]::GetPathRoot($PSScriptRoot) -ine 'D:\'){throw 'Исходники должны находиться на D'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$folder=Join-Path $PSScriptRoot 'dependencies'
[void][IO.Directory]::CreateDirectory($folder)
foreach($entry in (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Dependencies.json') -Raw | ConvertFrom-Json)){
 $archive=Join-Path $folder $entry.file
 if(!(Test-Path -LiteralPath $archive)){Invoke-WebRequest -UseBasicParsing -Uri $entry.url -OutFile $archive}
 if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $entry.sha256){throw ('Неверная SHA-256: '+$entry.id)}
 $destination=Join-Path $folder $entry.extract
 if(!(Test-Path -LiteralPath $destination)){
  if($entry.format -eq 'zip'){[IO.Compression.ZipFile]::ExtractToDirectory($archive,$destination)}
  else{[void][IO.Directory]::CreateDirectory($destination);& tar.exe -xzf $archive -C $destination;if($LASTEXITCODE){throw 'Не удалось распаковать FFmpeg'}}
 }
}
# Текст исключения GCC отсутствует в исходном архиве поставщика и включён в наш исходный комплект.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses\GCC-RUNTIME-LIBRARY-EXCEPTION.txt') -Destination (Join-Path $folder 'ffmpeg-release\GCC-RUNTIME-LIBRARY-EXCEPTION.txt') -Force
Write-Output 'Зависимости загружены на D и проверены по SHA-256'
