$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'system\common.ps1')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'system\security.ps1')
$checks=New-Object 'Collections.Generic.List[string]'
function Check([bool]$v,[string]$name){if(-not$v){throw $name};$checks.Add($name)}
$provider=[pscustomobject]@{displayName='??????';pathToSignedProductExe='C:\Program Files\HONOR\PCManager\qspublic\QAXDefenderHelper.exe';productState=266240}
$label=Get-GuardProviderLabel $provider
Check ($label.name-like'HONOR PC Manager*') 'Повреждённая запись сопоставлена с компонентом HONOR'
Check ($label.registeredName-ceq'??????'-and$provider.displayName-ceq'??????') 'Исходная регистрация не изменена'
Check ($label.nameRecovered-and$label.state-eq266240) 'Восстановление подписи не меняет состояние защиты'
$provider.displayName='Наш антивирус';$label=Get-GuardProviderLabel $provider
Check ($label.name-ceq'Наш антивирус'-and-not$label.nameRecovered) 'Правильная кириллица сохраняется'
$provider.displayName='IObit Malware Fighter';$label=Get-GuardProviderLabel $provider
Check ($label.name-ceq'IObit Malware Fighter'-and-not$label.nameRecovered) 'Нормальное имя IObit не подменяется'
$result=[pscustomobject]@{checks=$checks.Count;results=$checks.ToArray();realAntivirusRegistrationChanged=$false}
Write-GuardJson (Join-Path $GuardData 'Tests\provider-results.json') $result
$result|ConvertTo-Json -Depth 4
