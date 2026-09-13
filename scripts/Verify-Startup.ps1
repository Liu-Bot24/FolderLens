[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppRoot,[Parameter(Mandatory=$true)][string]$EvidenceRoot)
. "$PSScriptRoot\Common.ps1"
$AppRoot=Resolve-ProjectPath $AppRoot
$EvidenceRoot=Resolve-ProjectPath $EvidenceRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a new evidence directory for each startup verification.'}
$null=Invoke-LoggedProcess (Join-Path $AppRoot 'FolderLens.App.exe') @('--verify-refresh','--data-dir',$EvidenceRoot) ($EvidenceRoot+'-startup')
$report=Get-Content -LiteralPath (Join-Path $EvidenceRoot 'native-refresh.json') -Raw | ConvertFrom-Json
if($report.status -ne 'PASS' -or -not $report.directoryTreeAndHistory -or -not $report.recursiveEnableScannedChildren -or $report.missingThumbnailFrames -ne 0){throw 'Published folder browsing, thumbnail, preview and navigation verification failed.'}
Write-Host 'PASS: published folder scan, thumbnails, preview and navigation verified with isolated test data.'
