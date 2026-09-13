[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppRoot,[Parameter(Mandatory=$true)][string]$EvidenceRoot)
. "$PSScriptRoot\Common.ps1"
$AppRoot=Resolve-ProjectPath $AppRoot
$EvidenceRoot=Resolve-ProjectPath $EvidenceRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a new evidence directory for each startup verification.'}
$null=Invoke-LoggedProcess (Join-Path $AppRoot 'FolderLens.App.exe') @('--verify-refresh','--verify-fit-lock','--data-dir',$EvidenceRoot) ($EvidenceRoot+'-startup')
$report=Get-Content -LiteralPath (Join-Path $EvidenceRoot 'native-refresh.json') -Raw | ConvertFrom-Json
if($report.status -ne 'PASS' -or @($report.failures).Count -ne 0){throw 'Published main-window startup and fit controls verification failed.'}
Write-Host 'PASS: published main window loaded; native fit controls verified with isolated test data.'
