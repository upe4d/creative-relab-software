param([string]$Application='D:\PC Tune')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
# Полный исходный код без сборочных кэшей, личных настроек и журналов.
$version=([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'optimizerDuck\optimizerDuck.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object {$_} | Select-Object -First 1
$sourceName='pc-tune-'+$version+'-source.zip'
$sourceZip=Join-Path 'D:\Codex\work' $sourceName
$sourceRoot=$PSScriptRoot
$excluded=@('bin','obj','.git','.vs','Data','node_modules','.rtk')
$stream=[IO.File]::Open($sourceZip,[IO.FileMode]::Create,[IO.FileAccess]::Write)
$archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
try {
 foreach($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force){
  $rel=[IO.Path]::GetRelativePath($sourceRoot,$file.FullName)
  if(@($rel.Split([IO.Path]::DirectorySeparatorChar)|Where-Object {$_ -in $excluded}).Count){continue}
  if($file.Extension -in @('.user','.suo')-or$file.Name-eq'selected-results.xml'){continue}
  [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$rel.Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal)|Out-Null
 }
}finally{$archive.Dispose();$stream.Dispose()}
Copy-Item -LiteralPath $sourceZip -Destination (Join-Path $Application $sourceName) -Force
$evidence=Get-Content -LiteralPath (Join-Path $Application 'Data\Verification\results.json') -Raw|ConvertFrom-Json
if($evidence.failures-ne0){throw 'Диагностика приложения не прошла'}
$test=[xml](Get-Content -LiteralPath (Join-Path $sourceRoot 'optimizerDuck.Test\selected-results.xml') -Raw)
if([int]$test.assemblies.assembly.failed-ne0-or[int]$test.assemblies.assembly.errors-ne0-or[int]$test.assemblies.assembly.total-lt46){throw 'Нет успешного отчёта выбранных тестов'}
$summary=[ordered]@{
 version=$version;upstream='https://github.com/itsfatduck/optimizerDuck';commit='dd3b21ced1bdb06913afee6ad24c50af754e8f66';
 recordedAt=[DateTimeOffset]::Now.ToString('o');uiChecks=$evidence.checks;uiFailures=$evidence.failures;
 selectedTests=[int]$test.assemblies.assembly.total;selectedTestFailures=[int]$test.assemblies.assembly.failed;systemTweaksExecuted=$false;all42TweaksRuntimeValidated=$false;
 executableSHA256=(Get-FileHash -LiteralPath (Join-Path $Application 'PC Tune.exe') -Algorithm SHA256).Hash;
 assemblySHA256=(Get-FileHash -LiteralPath (Join-Path $Application 'optimizerDuck.dll') -Algorithm SHA256).Hash;
 sourceArchiveSHA256=(Get-FileHash -LiteralPath $sourceZip -Algorithm SHA256).Hash;
}
[IO.File]::WriteAllText((Join-Path $Application 'VERIFICATION.json'),($summary|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
# В распространяемый архив не входят результаты чтения оборудования и личной автозагрузки.
$portable=Join-Path 'D:\Codex\work' ('pc-tune-'+$version+'-portable.zip')
$stream=[IO.File]::Open($portable,[IO.FileMode]::Create,[IO.FileAccess]::Write)
$archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
try{
 foreach($file in Get-ChildItem -LiteralPath $Application -File -Recurse){
  $rel=[IO.Path]::GetRelativePath($Application,$file.FullName)
  if($rel.StartsWith('Data\',[StringComparison]::OrdinalIgnoreCase)){continue}
  [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$rel.Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal)|Out-Null
 }
}finally{$archive.Dispose();$stream.Dispose()}
$read=[IO.Compression.ZipFile]::OpenRead($portable)
try{
 foreach($name in @('PC Tune.exe','optimizerDuck.dll','LICENSE',$sourceName,'VERIFICATION.json')){
  if(!$read.GetEntry($name)){throw "Нет обязательного файла: $name"}
 }
 if(@($read.Entries|Where-Object {$_.FullName -like 'Data/Verification/*'}).Count){throw 'В архив попали частные результаты'}
 $entries=$read.Entries.Count
}finally{$read.Dispose()}
@{portable=$portable;files=$entries;source=$sourceZip;checks=$evidence.checks;failures=$evidence.failures;portableSHA256=(Get-FileHash -LiteralPath $portable -Algorithm SHA256).Hash}|ConvertTo-Json