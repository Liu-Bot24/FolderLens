[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
. "$PSScriptRoot\Common.ps1"
$EvidenceRoot=Resolve-ProjectPath $EvidenceRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a new fixture directory.'}
$app=Join-Path $EvidenceRoot 'app'
New-Item -ItemType Directory -Path $app | Out-Null
# Deliberately inert files: this tests the real manifest validator, not execution.
$files=@('App.xbf','MainWindow.xbf','FolderLens.App.pri')
foreach($worker in @('scan-worker/FolderLens.Scan.Worker','content-worker/FolderLens.Content.Worker','workers/FolderLens.Media.Worker')){
 foreach($extension in @('.exe','.dll','.deps.json','.runtimeconfig.json')){$files+=$worker+$extension}
}
foreach($file in $files){$path=Join-Path $app $file;New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null;[IO.File]::WriteAllText($path,'manifest-fixture')}
Write-JsonFile @{runtimeOptions=@{includedFrameworks=@(@{name='Microsoft.NETCore.App';version='10.0.0'})}} (Join-Path $app 'FolderLens.App.runtimeconfig.json')
Write-JsonFile @{buildId='manifest-fixture';workerSha256=(Get-FileHash (Join-Path $app 'workers/FolderLens.Media.Worker.exe')).Hash;providerLoadStatus='PASS'} (Join-Path $app 'CAPABILITIES.json')
Write-JsonFile @{managed=@('fixture');native=@('fixture')} (Join-Path $app 'SBOM.json')
function Write-FixtureManifest {
 Write-JsonFile @{buildId='manifest-fixture';requiredFiles=@('content-worker/FolderLens.Content.Worker.exe');files=Get-ReleaseFiles $app;gates=@{runtime='NOT_RUN'}} (Join-Path $EvidenceRoot 'release-manifest.json')
}
Write-FixtureManifest
& "$PSScriptRoot\Verify-Release.ps1" -ArtifactRoot $EvidenceRoot -ManifestOnly
$missing=Join-Path $app 'content-worker/FolderLens.Content.Worker.dll'
Move-Item -LiteralPath $missing -Destination (Join-Path $EvidenceRoot 'held-content.dll')
Write-FixtureManifest
$rejected=$false
try{& "$PSScriptRoot\Verify-Release.ps1" -ArtifactRoot $EvidenceRoot -ManifestOnly}
catch{if($_.Exception.Message -notmatch 'content-worker|Content.Worker'){throw};$rejected=$true;Write-Output $_.Exception.Message}
if(-not $rejected){throw 'Missing content DLL passed a manifest regenerated from the incomplete tree.'}
Write-Host 'PASS: complete worker layout accepted; incomplete content worker rejected despite matching hashes.'
