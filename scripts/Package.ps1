[CmdletBinding()]
param([string]$ArtifactRoot,[switch]$Help)
if($Help){Write-Output 'Package.ps1 [-ArtifactRoot <Publish output>]: packages the exact candidate manifest into portable ZIP and per-user unsigned Inno EXE. No builds, downloads, installs or GO claims.';exit 0}
. "$PSScriptRoot\Common.ps1"
if(-not $ArtifactRoot){$ArtifactRoot=(Get-Content (Join-Path $script:ProjectRoot 'artifacts\publish\latest-candidate.json') -Raw | ConvertFrom-Json).artifactRoot}
$ArtifactRoot=Resolve-ProjectPath $ArtifactRoot
$manifest=Get-Content (Join-Path $ArtifactRoot 'release-manifest.json') -Raw | ConvertFrom-Json
$appRoot=Join-Path $ArtifactRoot 'app'
$packageFiles=@(Get-PackageFiles $appRoot $manifest)
$shell=Get-ScriptShell
$null=Invoke-LoggedProcess $shell @('-NoProfile','-ExecutionPolicy','Bypass','-File',"$PSScriptRoot\Verify-Release.ps1",'-ArtifactRoot',$ArtifactRoot,'-ManifestOnly') (Join-Path $ArtifactRoot 'logs\before-package')
$null=Invoke-LoggedProcess $shell @('-NoProfile','-ExecutionPolicy','Bypass','-File',"$PSScriptRoot\Package-Source.ps1",'-ArtifactRoot',$ArtifactRoot) (Join-Path $ArtifactRoot 'logs\source-package')
$packageRoot=Join-Path $script:ProjectRoot ('artifacts\packages\'+$manifest.buildId)
New-Item -ItemType Directory -Path $packageRoot | Out-Null
$zip=Join-Path $packageRoot ('FolderLens-'+$manifest.buildId+'-win-x64-portable-unverified.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
try{
 foreach($file in $packageFiles){$null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.fullPath,('app/'+$file.path),[IO.Compression.CompressionLevel]::Optimal)}
 $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $ArtifactRoot 'release-manifest.json'),'release-manifest.json',[IO.Compression.CompressionLevel]::Optimal)
}finally{$archive.Dispose()}
$iscc=Join-Path $script:ProjectRoot '.tools\inno\ISCC.exe'
$native=Get-Content (Join-Path $script:ProjectRoot 'native\dependency-lock.json') -Raw | ConvertFrom-Json
$inno=@($native.components | Where-Object {$_.id -eq 'InnoSetup'})[0]
if((Get-FileHash -LiteralPath $iscc).Hash -ne $inno.sha256){throw 'Inno compiler differs from native/dependency-lock.json.'}
$sourceFileList=Join-Path $packageRoot 'installer-files.iss'
[IO.File]::WriteAllLines($sourceFileList,[string[]]@(Get-InnoPackageFileLines $packageFiles),(New-Object Text.UTF8Encoding($true)))
$null=Invoke-LoggedProcess $iscc @(('/DAppSource='+$appRoot),('/DSourceFileList='+$sourceFileList),('/DOutputRoot='+$packageRoot),('/DBuildId='+$manifest.buildId),(Join-Path $script:ProjectRoot 'installer\FolderLens.iss')) (Join-Path $ArtifactRoot 'logs\inno-package')
$exe=Join-Path $packageRoot ('FolderLens-'+$manifest.buildId+'-win-x64-setup-unsigned.exe')
if(-not(Test-Path -LiteralPath $exe)){throw 'Inno returned success without expected setup EXE.'}
$signature=Get-AuthenticodeSignature -LiteralPath $exe
if($signature.Status -ne 'NotSigned'){throw "Unexpected candidate signature state: $($signature.Status)"}
Copy-Item -LiteralPath (Join-Path $ArtifactRoot 'release-manifest.json') -Destination $packageRoot
$sourceManifest=Get-Content (Join-Path $ArtifactRoot 'source\source-manifest.json') -Raw | ConvertFrom-Json
$sourceZip=Join-Path $packageRoot $sourceManifest.file
Copy-Item -LiteralPath (Join-Path $ArtifactRoot ('source\'+$sourceManifest.file)) -Destination $sourceZip
Copy-Item -LiteralPath (Join-Path $ArtifactRoot 'source\source-manifest.json') -Destination $packageRoot
$packages=@(Get-Item -LiteralPath $zip,$exe,$sourceZip | ForEach-Object {[pscustomobject]@{file=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
Write-JsonFile ([ordered]@{buildId=$manifest.buildId;artifactRoot=$ArtifactRoot;status='CANDIDATE_UNVERIFIED';setupSignature='NotSigned';files=$packages;installation='NOT_RUN';cleanMachine='NOT_RUN';releaseVerdict='NO-GO'}) (Join-Path $packageRoot 'package-manifest.json')
[IO.File]::WriteAllLines((Join-Path $packageRoot 'SHA256SUMS.txt'),[string[]]@($packages | ForEach-Object {$_.sha256+'  '+$_.file}),(New-Object Text.UTF8Encoding($false)))
Write-JsonFile ([ordered]@{artifactRoot=$packageRoot;publishRoot=$ArtifactRoot;buildId=$manifest.buildId}) (Join-Path $script:ProjectRoot 'artifacts\packages\latest-candidate.json')
Write-Host "Unsigned candidate packages generated: $packageRoot. Installation and release acceptance remain NOT_RUN/NO-GO."
