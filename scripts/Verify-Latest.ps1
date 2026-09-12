[CmdletBinding()]
param([switch]$Help)
if($Help){Write-Output 'Verify-Latest.ps1: invokes Verify-Release for the last generated package.';exit 0}
. "$PSScriptRoot\Common.ps1"
$latest=Get-Content (Join-Path $script:ProjectRoot 'artifacts\packages\latest-candidate.json') -Raw | ConvertFrom-Json
& "$PSScriptRoot\Verify-Release.ps1" -ArtifactRoot $latest.artifactRoot
