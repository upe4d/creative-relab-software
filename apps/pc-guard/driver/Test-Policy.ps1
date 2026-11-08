param([string]$Toolchain='D:\Codex\work\PCGuard-toolchain')
$ErrorActionPreference='Stop'
$bin=Join-Path $Toolchain 'msvc\Contents\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64'
$out=Join-Path $PSScriptRoot 'build';[void][IO.Directory]::CreateDirectory($out)
$definition="LIBRARY kernel32.dll`nEXPORTS`nExitProcess`nGetStdHandle`nWriteFile`nVirtualAlloc`nVirtualProtect`nVirtualFree`n"
[IO.File]::WriteAllText((Join-Path $out 'kernel32.def'),$definition,[Text.Encoding]::ASCII)
& (Join-Path $bin 'lib.exe') /nologo /machine:x64 ('/def:'+(Join-Path $out 'kernel32.def')) ('/out:'+(Join-Path $out 'kernel32.lib')) | Out-Null
foreach($name in @('policy','test_policy')){
    & (Join-Path $bin 'cl.exe') /nologo /c /TC /W4 /WX /GS- /O2 /Zl /utf-8 ('/Fo'+(Join-Path $out ($name+'-test.obj'))) (Join-Path $PSScriptRoot ($name+'.c'))
    if($LASTEXITCODE-ne0){throw 'Не собран стенд политик.'}
}
& (Join-Path $bin 'link.exe') /nologo /machine:x64 /subsystem:console /entry:mainCRTStartup /nodefaultlib ('/out:'+(Join-Path $out 'policy-tests.exe')) (Join-Path $out 'policy-test.obj') (Join-Path $out 'test_policy-test.obj') (Join-Path $out 'kernel32.lib')
if($LASTEXITCODE-ne0){throw 'Не скомпонован стенд политик.'}
$result=& (Join-Path $out 'policy-tests.exe');if($LASTEXITCODE-ne0){throw ('Не пройдены политики: '+$result)}
[IO.File]::WriteAllText((Join-Path $out 'policy-test-results.json'),$result,[Text.UTF8Encoding]::new($false));$result
