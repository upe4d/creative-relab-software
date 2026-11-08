param([string]$Toolchain='D:\Codex\work\PCGuard-toolchain')
$ErrorActionPreference='Stop'
$binary=Join-Path $PSScriptRoot 'build\PCGuardFilter.sys'
$signature=Get-AuthenticodeSignature -LiteralPath $binary
$metadata=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release.json') -Raw|ConvertFrom-Json
$valid=$false
if($signature.Status-eq'Valid'){
    $tool=Join-Path $Toolchain 'sdk\c\bin\10.0.28000.0\x64\signtool.exe'
    if(-not(Test-Path -LiteralPath $tool)){throw 'Нет официального signtool для проверки kernel policy.'}
    & $tool verify /kp /q $binary | Out-Null;$valid=$LASTEXITCODE-eq0
}
$binaryHash=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
$tested=[bool]$metadata.kernelRuntimeTested-and$metadata.kernelTestSha256-eq$binaryHash
$result=[ordered]@{Binary=$binary;SHA256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash;Signature=[string]$signature.Status;KernelSigningPolicyPassed=$valid;AssignedAltitude=$metadata.assignedAltitude;KernelRuntimeTested=$tested;InfPackageValidated=[bool]$metadata.infPackageValidated;ReadyForInstallation=($valid-and$metadata.assignedAltitude-match'\A[0-9]+(?:\.[0-9]+)?\z'-and$tested-and$metadata.infPackageValidated);DriverLoadedByThisCheck=$false}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'build\release-check.json'),($result|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$result|ConvertTo-Json
