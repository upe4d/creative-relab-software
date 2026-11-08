param([string]$Toolchain='D:\Codex\work\PCGuard-toolchain')
$ErrorActionPreference='Stop'
$compiler=Join-Path $Toolchain 'msvc\Contents\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\cl.exe'
$linker=Join-Path $Toolchain 'msvc\Contents\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\link.exe'
$wdk=Join-Path $Toolchain 'wdk\c';$sdk=Join-Path $Toolchain 'sdk\c';$version='10.0.28000.0'
$out=Join-Path $PSScriptRoot 'build';[void][IO.Directory]::CreateDirectory($out)
# Заголовки WDK считаются внешними: строгие предупреждения применяются к собственному C.
$options=@('/nologo','/c','/TC','/W4','/WX','/GS','/Zi',('/Fd'+(Join-Path $out 'compile.pdb')),'/O2','/Zl','/kernel','/utf-8','/D_AMD64_','/DAMD64','/DWINVER=0x0A00','/D_WIN32_WINNT=0x0A00','/DNTDDI_VERSION=0x0A00000C','/DPCG_KERNEL','/external:W0')
foreach($path in @((Join-Path $wdk "Include\$version\km"),(Join-Path $wdk "Include\$version\km\crt"),(Join-Path $sdk "Include\$version\shared"),(Join-Path $sdk "Include\$version\ucrt"),(Join-Path $Toolchain 'msvc\Contents\VC\Tools\MSVC\14.44.35207\include'))){$options+=('/external:I'+$path)}
$objects=@()
foreach($name in @('policy','process','files','streams','control','objects','driver')){
    $obj=Join-Path $out ($name+'.obj')
    & $compiler @options ('/Fo'+$obj) (Join-Path $PSScriptRoot ($name+'.c'))
    if($LASTEXITCODE-ne0){throw ('Ошибка компиляции '+$name)}
    $objects+=$obj
}
$libraries=Join-Path $wdk "Lib\$version\km\x64"
& $linker '/nologo' '/driver' '/subsystem:native,10.00' '/entry:GsDriverEntry' '/machine:x64' '/nodefaultlib' '/debug:full' ('/pdb:'+(Join-Path $out 'PCGuardFilter.pdb')) '/integritycheck' '/dynamicbase' '/nxcompat' '/opt:ref' '/opt:icf' ('/out:'+(Join-Path $out 'PCGuardFilter.sys')) ('/libpath:'+$libraries) $objects 'ntoskrnl.lib' 'fltMgr.lib' 'cng.lib' 'wdmsec.lib' 'BufferOverflowFastFailK.lib'
if($LASTEXITCODE-ne0){throw 'Ошибка компоновки драйвера.'}
Get-FileHash -LiteralPath (Join-Path $out 'PCGuardFilter.sys') -Algorithm SHA256
