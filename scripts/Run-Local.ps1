[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release',[string]$AppRoot,[switch]$Help)
if($Help){Write-Output 'Run-Local.ps1 [-AppRoot <app-directory>]: starts the current delivered entry from artifacts/publish/latest-candidate.json. No fallback to bin, build, download, or install. Configuration is retained for command compatibility.';exit 0}
. "$PSScriptRoot\Common.ps1"
if(-not $AppRoot){
 $pointer=Join-Path $script:ProjectRoot 'artifacts\publish\latest-candidate.json'
 if(-not(Test-Path -LiteralPath $pointer)){throw 'No delivered entry is configured. An explicit -AppRoot is required for a developer build.'}
 $current=Get-Content -LiteralPath $pointer -Raw | ConvertFrom-Json
 $AppRoot=Join-Path $current.artifactRoot 'app'
 $identity=Get-Content -LiteralPath (Join-Path $AppRoot 'BUILD-INFO.json') -Raw | ConvertFrom-Json
 if($current.buildId -ne $identity.buildId){throw 'Delivered entry identity differs from its pointer.'}
}
$AppRoot=Resolve-ProjectPath $AppRoot
$exe=Join-Path $AppRoot 'FolderLens.App.exe'
if(-not(Test-Path -LiteralPath $exe)){throw "Application is not built: $exe"}
# A visible window is the explicit purpose of this command.
$process=Start-Process -FilePath $exe -WorkingDirectory $AppRoot -PassThru
Write-Host "Started FolderLens (PID $($process.Id)); this is not UI acceptance."
