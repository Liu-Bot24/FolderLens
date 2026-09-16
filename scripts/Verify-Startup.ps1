[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppRoot,[Parameter(Mandatory=$true)][string]$EvidenceRoot,[ValidateRange(1,600)][int]$TimeoutSeconds=120)
. "$PSScriptRoot\Common.ps1"
$AppRoot=Resolve-ProjectPath $AppRoot
$EvidenceRoot=Resolve-ProjectPath $EvidenceRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a new evidence directory for each startup verification.'}
foreach($required in Get-RequiredWorkerFiles){if(-not(Test-Path -LiteralPath (Join-Path $AppRoot $required) -PathType Leaf)){throw "Incomplete application worker: $required"}}
$null=Invoke-LoggedProcess (Join-Path $AppRoot 'FolderLens.App.exe') @('--verify-refresh','--verify-deployed','--data-dir',$EvidenceRoot) ($EvidenceRoot+'-startup') -TimeoutSeconds $TimeoutSeconds
$report=Get-Content -LiteralPath (Join-Path $EvidenceRoot 'native-refresh.json') -Raw | ConvertFrom-Json
if($report.status -ne 'PASS' -or -not $report.directoryTreeAndHistory -or -not $report.browseDepthReusesScannedChildren -or $report.missingThumbnailFrames -ne 0){throw 'Published folder browsing, thumbnail, preview and navigation verification failed.'}
function Assert-DeployedComponents($Report) {
 if(-not $Report.deployedVerification){throw 'Startup verification allowed development fallback.'}
 $expected=@{media='workers/FolderLens.Media.Worker.exe';content='content-worker/FolderLens.Content.Worker.exe';scan='scan-worker/FolderLens.Scan.Worker.exe';ffprobe='native/ffmpeg/ffprobe.exe'}
 foreach($name in $expected.Keys){if($Report.components.$name.Replace('\','/') -ne $expected[$name]){throw "Unexpected verification component: $name"}}
}
Assert-DeployedComponents $report
foreach($case in @('scan-pipeline','promotion-viewport','text-reader')){
 $caseRoot=$EvidenceRoot+'-'+$case
 $null=Invoke-LoggedProcess (Join-Path $AppRoot 'FolderLens.App.exe') @('--verify-refresh','--verify-deployed',('--verify-'+$case),'--data-dir',$caseRoot) ($caseRoot+'-run') -TimeoutSeconds $TimeoutSeconds
 $caseReport=Get-Content -LiteralPath (Join-Path $caseRoot 'native-refresh.json') -Raw | ConvertFrom-Json
 if($caseReport.status -ne 'PASS'){throw "Published $case verification failed."}
 Assert-DeployedComponents $caseReport
}
Write-Host 'PASS: published folder scan, thumbnails, preview, TXT/Markdown reading and navigation verified without development fallback.'
