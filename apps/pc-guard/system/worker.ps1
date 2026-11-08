param([Parameter(Mandatory=$true)][ValidatePattern('^[a-f0-9]{32}$')][string]$JobId)
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'security.ps1')
. (Join-Path $PSScriptRoot 'network.ps1')
. (Join-Path $PSScriptRoot 'startup.ps1')
. (Join-Path $PSScriptRoot 'camera.ps1')
. (Join-Path $PSScriptRoot 'camera-policy.ps1')
. (Join-Path $PSScriptRoot 'camera-consent.ps1')
$requestPath=Join-Path $GuardData ('Jobs\'+$JobId+'.request.json')
$resultPath=Join-Path $GuardData ('Jobs\'+$JobId+'.result.json')
$request=Get-GuardJson $requestPath $null
if(-not $request -or $request.id -ne $JobId){throw 'Некорректное задание.'}
$script:GuardResult=[ordered]@{id=$JobId;action=$request.action;created=$request.created;state='running';message='Выполняется системная операция';data=$null}
function Set-GuardProgress([string]$Message){$script:GuardResult.message=$Message;Write-GuardJson $resultPath $script:GuardResult}
try{
    Set-GuardProgress 'Обращение к Windows'
    $value=switch($request.action){
        'status'{Get-GuardSecurity}
        'startup'{Get-GuardStartup}
        'network'{Get-GuardNetwork}
        'threats'{Get-GuardThreats}
        'file'{Get-GuardFileAnalysis $request.arguments}
        'pick'{Select-GuardPath $request.arguments}
        'scan'{Invoke-GuardScan $request.arguments}
        'update'{Update-GuardSignatures}
        'startup_disable'{Disable-GuardStartup $request.arguments}
        'startup_restore'{Restore-GuardStartup $request.arguments}
        'open'{Open-GuardWindow $request.arguments}
        'camera'{Get-GuardCameras}
        'camera_apps'{Get-GuardCameraUserConsent}
        'camera_app_deny'{Set-GuardCameraUserConsent $request.arguments $false}
        'camera_app_restore'{Set-GuardCameraUserConsent $request.arguments $true}
        'camera_disable'{Set-GuardCamera $request.arguments $false}
        'camera_restore'{Set-GuardCamera $request.arguments $true}
        default{throw 'Неизвестная операция Windows.'}
    }
    $script:GuardResult.state='done';$script:GuardResult.message='Операция завершена';$script:GuardResult.data=$value
}catch{$script:GuardResult.state='error';$script:GuardResult.message=$_.Exception.Message;$script:GuardResult.errorType=$_.Exception.GetType().FullName}
Write-GuardJson $resultPath $script:GuardResult
