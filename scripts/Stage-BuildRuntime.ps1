[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release',[string]$AppDirectory,[switch]$Help)
if($Help){Write-Output 'Stage-BuildRuntime.ps1 [-Configuration Debug|Release] [-AppDirectory <project-local-output>]: copies already-built self-contained workers and verified local native runtimes into the app output. Does not rebuild, download, or launch the app.';exit 0}
. "$PSScriptRoot\Common.ps1"
$appRoot=Join-Path $script:ProjectRoot "src\FolderLens.App\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64"
if($AppDirectory){
 $appRoot=Resolve-ProjectPath $AppDirectory
 $projectPrefix=[IO.Path]::GetFullPath($script:ProjectRoot).TrimEnd('\')+'\'
 if(-not $appRoot.StartsWith($projectPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Development runtime output must remain inside this project.'}
}
if(-not(Test-Path -LiteralPath (Join-Path $appRoot 'FolderLens.App.exe'))){throw 'App must be built before staging its runtime.'}
function Sync-BuiltTree([string]$Source,[string]$Destination){
 if(-not(Test-Path -LiteralPath $Source -PathType Container)){throw "Built/runtime input missing: $Source"}
 foreach($file in Get-ChildItem -LiteralPath $Source -Recurse -File){
  if($file.Extension -eq '.pdb'){continue}
  $target=Join-Path $Destination $file.FullName.Substring($Source.Length+1)
  if((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash){continue}
  New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
  Copy-Item -LiteralPath $file.FullName -Destination $target -Force
 }
}
$workers=@{'FolderLens.Media.Worker'='workers';'FolderLens.Content.Worker'='content-worker';'FolderLens.Scan.Worker'='scan-worker'}
foreach($name in $workers.Keys){
 $inputRoot=Join-Path $script:ProjectRoot "src\$name\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64"
 $runtime=Join-Path $inputRoot ($name+'.runtimeconfig.json')
 if(-not(Test-Path -LiteralPath $runtime)){throw "Self-contained worker not built: $name. RuntimeIdentifier=win-x64 and SelfContained=true are required."}
 $config=Get-Content -LiteralPath $runtime -Raw | ConvertFrom-Json
 if(-not $config.runtimeOptions.PSObject.Properties['includedFrameworks']){throw "Worker is not self-contained: $name"}
 Sync-BuiltTree $inputRoot (Join-Path $appRoot $workers[$name])
}
foreach($file in @('FolderLens.RawBridge.dll','libraw.dll')){Copy-Item -LiteralPath (Join-Path $script:ProjectRoot "artifacts\native-build\$Configuration\$file") -Destination (Join-Path $appRoot 'workers') -Force}
$lock=Get-Content (Join-Path $script:ProjectRoot 'native\dependency-lock.json') -Raw | ConvertFrom-Json
$crt=@($lock.components | Where-Object {$_.id -eq 'Microsoft.VC143.CRT'})[0]
$crtDirectory=Get-CrtDirectory $crt
foreach($file in $crt.files){$source=Join-Path $crtDirectory $file.file;Copy-Item -LiteralPath $source -Destination (Join-Path $appRoot 'workers') -Force}
Sync-BuiltTree (Join-Path $script:ProjectRoot 'native\ffmpeg') (Join-Path $appRoot 'native\ffmpeg')
Sync-BuiltTree (Join-Path $script:ProjectRoot '.tools\webview2-fixed\Microsoft.WebView2.FixedVersionRuntime.153.0.4234.32.x64') (Join-Path $appRoot 'runtime\webview2')
Write-Host "Build runtime staged: $appRoot. Clean release packaging still uses a fresh Publish directory."
