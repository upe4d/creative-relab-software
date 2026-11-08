param([string]$Toolchain='D:\Codex\work\PCGuard-toolchain',[switch]$PrepareOnly)
$ErrorActionPreference='Stop'
$out=Join-Path $PSScriptRoot 'build\lab-package-v3';[void][IO.Directory]::CreateDirectory($out)
$source=Join-Path $PSScriptRoot 'build\PCGuardFilter.sys';Copy-Item -LiteralPath $source -Destination (Join-Path $out 'PCGuardFilter.sys') -Force
# Временная altitude применяется только в стенде; CAT создаётся без подписи и не устанавливается.
$date=[DateTime]::UtcNow.ToString('MM/dd/yyyy',[Globalization.CultureInfo]::InvariantCulture)
$inf=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'PCGuardFilter.lab.inf.in')).Replace('DRIVERDATE',$date)
[IO.File]::WriteAllText((Join-Path $out 'PCGuardFilter.inf'),$inf,[Text.Encoding]::Unicode)
if($PrepareOnly){
 $prepared=@{prepared=$true;binarySha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash;inf2catPassed=$null;catalogSigned=$false;testOnly=$true;assignedAltitude=$false;installed=$false;kernelRuntimeTested=$false}
 [IO.File]::WriteAllText((Join-Path $out 'prepared-package.json'),($prepared|ConvertTo-Json),[Text.UTF8Encoding]::new($false));$prepared|ConvertTo-Json;return
}
$tool=Join-Path $Toolchain 'wdk\c\bin\10.0.28000.0\x86\Inf2Cat.exe'
$messages=& $tool ('/driver:'+$out) '/os:10_GE_X64,10_25H2_X64' '/verbose' 2>&1;$exit=$LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $out 'inf2cat.log'),($messages|Out-String),[Text.UTF8Encoding]::new($false))
$catalog=Join-Path $out 'PCGuardFilter.cat';$result=@{exit=$exit;inf2catPassed=($exit-eq0-and(Test-Path -LiteralPath $catalog));binarySha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash;targetWindows=@('Windows 11 24H2 x64','Windows 11 25H2 x64');catalogSigned=$false;testOnly=$true;assignedAltitude=$false;installed=$false;kernelRuntimeTested=$false}
[IO.File]::WriteAllText((Join-Path $out 'package-validation.json'),($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$messages|Select-Object -Last 20;$result|ConvertTo-Json -Depth 5
if(-not$result.inf2catPassed){throw 'Тестовый INF/CAT не прошёл Inf2Cat.'}