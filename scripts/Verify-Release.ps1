[CmdletBinding()]
param([string]$ArtifactRoot,[switch]$ManifestOnly,[switch]$RequireGo,[switch]$Help)
if($Help){Write-Output 'Verify-Release.ps1 -ArtifactRoot <publish-or-package-directory> [-ManifestOnly] [-RequireGo]. Validates exact manifest hashes, runtime files, candidate metadata and package ZIP entries. Never installs or replaces actual UI/clean-machine acceptance. RequireGo fails candidates.';exit 0}
. "$PSScriptRoot\Common.ps1"
if(-not $ArtifactRoot){throw '-ArtifactRoot is required.'}
$ArtifactRoot=Resolve-ProjectPath $ArtifactRoot
$packageFile=Join-Path $ArtifactRoot 'package-manifest.json'
$package=$null
$publishRoot=$ArtifactRoot
if(Test-Path -LiteralPath $packageFile){$package=Get-Content -LiteralPath $packageFile -Raw | ConvertFrom-Json;$publishRoot=$package.artifactRoot}
$manifest=Get-Content (Join-Path $publishRoot 'release-manifest.json') -Raw | ConvertFrom-Json
$appRoot=Join-Path $publishRoot 'app'
$null=@(Get-PackageFiles $appRoot $manifest)
$expected=@{}
foreach($file in $manifest.files){
 if($file.path -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$))'){throw "Unsafe manifest path: $($file.path)"}
 $path=Join-Path $appRoot $file.path
 if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Required manifest file missing: $($file.path)"}
 $actual=Get-Item -LiteralPath $path
 if($actual.Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256){throw "Manifest hash mismatch: $($file.path)"}
 $expected[$file.path]=$file
}
foreach($required in $manifest.requiredFiles){if(-not $expected.ContainsKey($required)){throw "Required runtime file missing from manifest: $required"}}
foreach($required in @('App.xbf','MainWindow.xbf','FolderLens.App.pri')){
 if(-not $expected.ContainsKey($required)){throw "Required application XAML resource missing from manifest: $required"}
}
foreach($worker in @('FolderLens.Scan.Worker','FolderLens.Content.Worker','FolderLens.Media.Worker')){
 if(Test-Path -LiteralPath (Join-Path $appRoot ($worker+'.exe'))){throw "Unexpected worker entry in application root: $worker"}
}
foreach($required in Get-RequiredWorkerFiles){
 if(-not $expected.ContainsKey($required)){throw "Incomplete published worker: $required"}
}
$actualFiles=Get-ReleaseFiles $appRoot
if($actualFiles.Count -ne $manifest.files.Count){throw 'Unexpected files exist in candidate application tree.'}
foreach($file in $actualFiles){
 if(-not $expected.ContainsKey($file.path)){throw "Unexpected file: $($file.path)"}
 if(Test-PrivateReleasePath $file.path){throw "Private or development data in release: $($file.path)"}
}
$config=Get-Content (Join-Path $appRoot 'FolderLens.App.runtimeconfig.json') -Raw | ConvertFrom-Json
if(-not $config.runtimeOptions.PSObject.Properties['includedFrameworks']){throw 'App runtimeconfig is not .NET self-contained.'}
$cap=Get-Content (Join-Path $appRoot 'CAPABILITIES.json') -Raw | ConvertFrom-Json
if($cap.buildId -ne $manifest.buildId){throw 'Capability report build identity mismatch.'}
if($cap.workerSha256 -ne (Get-FileHash -LiteralPath (Join-Path $appRoot 'workers\FolderLens.Media.Worker.exe')).Hash){throw 'Capability report media binary mismatch.'}
if($cap.providerLoadStatus -ne 'PASS'){throw 'Published provider load probe failed.'}
$sbom=Get-Content (Join-Path $appRoot 'SBOM.json') -Raw | ConvertFrom-Json
if(@($sbom.managed).Count -eq 0 -or @($sbom.native).Count -eq 0){throw 'SBOM is empty.'}
if($package -and -not $ManifestOnly){
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 foreach($file in $package.files){
  $path=Join-Path $ArtifactRoot $file.file
  if((Get-FileHash -LiteralPath $path).Hash -ne $file.sha256){throw "Package hash mismatch: $($file.file)"}
  if($file.file.EndsWith('-portable-unverified.zip')){
   $zip=[IO.Compression.ZipFile]::OpenRead($path)
   try{
    if($zip.Entries.Count -ne $manifest.files.Count+1){throw 'Portable ZIP entry count mismatch.'}
    foreach($entry in $zip.Entries){
     if($entry.FullName -eq 'release-manifest.json'){continue}
     if(-not $entry.FullName.StartsWith('app/')){throw 'Unexpected ZIP root entry.'}
     $relative=$entry.FullName.Substring(4)
     if(-not $expected.ContainsKey($relative)){throw "Unexpected ZIP entry: $relative"}
     $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
     try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
     if($hash -ne $expected[$relative].sha256){throw "Portable ZIP content mismatch: $relative"}
    }
   }finally{$zip.Dispose()}
  }
 }
 if($package.buildId -ne $manifest.buildId){throw 'Package and publish identities differ.'}
}
$report=[ordered]@{utc=[DateTime]::UtcNow.ToString('o');buildId=$manifest.buildId;fileCount=$manifest.files.Count;integrity='PASS';packageContent=$(if($package -and -not $ManifestOnly){'PASS'}else{'NOT_RUN'});releaseVerdict='NO-GO';gates=$manifest.gates;scope='Hash/layout integrity only; not runtime feature, installation or performance acceptance.'}
Write-JsonFile $report (Join-Path $ArtifactRoot 'verification.json')
Write-Host "Integrity validation passed for $($manifest.files.Count) files. Full release: NO-GO; actual machine and feature gates remain incomplete."
if($RequireGo){throw 'Candidate is not GO. Required release gates remain unverified or blocked.'}
