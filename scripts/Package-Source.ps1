[CmdletBinding()]
param([string]$ArtifactRoot,[switch]$Help)
if($Help){Write-Output 'Package-Source.ps1 -ArtifactRoot <published candidate>: archives exact current matching build inputs plus product specs. Excludes Git internals, operator instructions, logs, dependencies caches, build products and private fixtures. Requires source fingerprint match.';exit 0}
. "$PSScriptRoot\Common.ps1"
if(-not $ArtifactRoot){throw '-ArtifactRoot is required.'}
$ArtifactRoot=Resolve-ProjectPath $ArtifactRoot
$manifest=Get-Content (Join-Path $ArtifactRoot 'release-manifest.json') -Raw | ConvertFrom-Json
$inputs=Get-BuildInputs
if($inputs.sha256 -ne $manifest.sourceSha256){throw 'Current source differs from published candidate; source archive must not be mislabeled.'}
$sourceFiles=@()
foreach($folder in @('src','tests','scripts','installer','contracts','planning','docs','assets','tools','native')){
 $directory=Join-Path $script:ProjectRoot $folder
 if(Test-Path -LiteralPath $directory){$sourceFiles+=Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -notin @('.exe','.dll','.pdb') -and $_.Name -notin @('AGENTS.md','AGENTS.override.md','DEVELOPMENT_LOG.md','PACKAGING_EVIDENCE.md')}}
}
$sourceFiles+=Get-ChildItem -LiteralPath $script:ProjectRoot -File | Where-Object {$_.Name -match '^(README.*|global\.json|Directory\..*\.props|NuGet\.Config|FolderLens\.slnx|build-release\.cmd|\.gitignore)$'}
$fixtureManifest=Join-Path $script:ProjectRoot 'fixtures\manifest.json'
if(Test-Path -LiteralPath $fixtureManifest){$sourceFiles+=Get-Item -LiteralPath $fixtureManifest}
$sourceRoot=Join-Path $ArtifactRoot 'source'
New-Item -ItemType Directory -Force -Path $sourceRoot | Out-Null
$zipPath=Join-Path $sourceRoot ('FolderLens-'+$manifest.buildId+'-source.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
try{
 foreach($file in $sourceFiles | Sort-Object FullName -Unique){
  $relative=$file.FullName.Substring($script:ProjectRoot.Length+1).Replace('\','/')
  $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,('FolderLens/'+$relative),[IO.Compression.CompressionLevel]::Optimal)
 }
}finally{$zip.Dispose()}
$after=Get-BuildInputs
if($after.sha256 -ne $inputs.sha256){throw 'Source changed while archiving; archive is incomplete/unverified and must not be distributed as matching.'}
Write-JsonFile ([ordered]@{buildId=$manifest.buildId;sourceSha256=$inputs.sha256;gitHead=$inputs.gitHead;file=[IO.Path]::GetFileName($zipPath);bytes=(Get-Item -LiteralPath $zipPath).Length;sha256=(Get-FileHash -LiteralPath $zipPath).Hash;dependencyArchivesIncluded=$false;sourceObligations='See native/dependency-lock.json; native caches/binaries/private fixtures are not source-archive contents.'}) (Join-Path $sourceRoot 'source-manifest.json')
Write-Host "Matching source archived: $zipPath"
