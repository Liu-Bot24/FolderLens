[CmdletBinding()]
param([ValidateSet('Release')][string]$Configuration='Release',[ValidateSet('win-x64')][string]$Runtime='win-x64',[switch]$Help)
if($Help){Write-Output 'Publish.ps1 [-Configuration Release] [-Runtime win-x64]: locked self-contained publish to a new artifacts/publish/<buildId> directory; includes media/content/scan workers, RawBridge, official CRT, FFmpeg and fixed WebView2. Produces a candidate manifest, never GO.';exit 0}
. "$PSScriptRoot\Common.ps1"
$inputs=Get-BuildInputs
$id='0.1.0-candidate-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+$inputs.sha256.Substring(0,12).ToLowerInvariant()
$releaseRoot=Join-Path $script:ProjectRoot ('artifacts\publish\'+$id)
$appRoot=Join-Path $releaseRoot 'app'
New-Item -ItemType Directory -Path $appRoot | Out-Null
$dotnet=Get-DotNet
$projects=@(
@{name='FolderLens.App';destination=$appRoot},
@{name='FolderLens.Media.Worker';destination=(Join-Path $appRoot 'workers')},
@{name='FolderLens.Content.Worker';destination=(Join-Path $appRoot 'content-worker')}
)
$scan=Join-Path $script:ProjectRoot 'src\FolderLens.Scan.Worker\FolderLens.Scan.Worker.csproj'
if(Test-Path -LiteralPath $scan){$projects+=@{name='FolderLens.Scan.Worker';destination=(Join-Path $appRoot 'scan-worker')}}
foreach($project in $projects){
 $projectFile='src\'+$project.name+'\'+$project.name+'.csproj'
 $output=Join-Path $releaseRoot ('publish-inputs\'+$project.name)
 $buildOutput=(Join-Path $releaseRoot ('build-output\'+$project.name))+'\'
 $runtimeCheck=Invoke-LoggedProcess $dotnet @('msbuild',$projectFile,'-getProperty:RuntimeIdentifier') (Join-Path $releaseRoot ('logs\runtime-'+$project.name))
 $projectRuntime=(Get-Content -LiteralPath $runtimeCheck.stdout -Raw).Trim()
 if($projectRuntime -ne $Runtime){throw "Project runtime '$projectRuntime' differs from requested '$Runtime': $projectFile"}
 # Executables declare their RID locally. A command-line -r also changes the RID of
 # shared libraries, which intentionally have RID-neutral locked restore graphs.
 $arguments=@('publish',$projectFile,'-c',$Configuration,'--self-contained','true','-p:RestoreLockedMode=true','-p:PublishTrimmed=false','-p:PublishSingleFile=false','-p:DebugType=None','-p:DebugSymbols=false','-p:ContinuousIntegrationBuild=true',('-p:OutDir='+$buildOutput),'-o',$output)
 $null=Invoke-LoggedProcess $dotnet $arguments (Join-Path $releaseRoot ('logs\publish-'+$project.name))
 Copy-VerifiedTree $output $project.destination
}
foreach($file in @('FolderLens.RawBridge.dll','libraw.dll')){
 $source=Join-Path $script:ProjectRoot ('artifacts\native-build\Release\'+$file)
 if(-not(Test-Path -LiteralPath $source)){throw 'Native release bridge is missing. Run Build.ps1 first.'}
 Copy-Item -LiteralPath $source -Destination (Join-Path $appRoot 'workers')
}
$nativeLock=Get-Content (Join-Path $script:ProjectRoot 'native\dependency-lock.json') -Raw | ConvertFrom-Json
$crt=@($nativeLock.components | Where-Object {$_.id -eq 'Microsoft.VC143.CRT'})[0]
$crtDirectory=Get-CrtDirectory $crt
foreach($file in $crt.files){
 $source=Join-Path $crtDirectory $file.file
 if((Get-FileHash -LiteralPath $source).Hash -ne $file.sha256){throw "CRT hash mismatch: $($file.file)"}
 Copy-Item -LiteralPath $source -Destination (Join-Path $appRoot 'workers')
}
foreach($entry in (Get-Content (Join-Path $script:ProjectRoot 'native\ffmpeg\sha256.json') -Raw | ConvertFrom-Json)){if((Get-FileHash -LiteralPath (Join-Path $script:ProjectRoot ('native\ffmpeg\'+$entry.file))).Hash -ne $entry.Hash){throw 'FFmpeg locked binary hash mismatch.'}}
Copy-VerifiedTree (Join-Path $script:ProjectRoot 'native\ffmpeg') (Join-Path $appRoot 'native\ffmpeg')
$webLock=@($nativeLock.components | Where-Object {$_.id -eq 'Microsoft.WebView2.FixedVersionRuntime'})[0]
if((Get-FileHash -LiteralPath (Resolve-ProjectPath $webLock.binary)).Hash -ne $webLock.binarySha256){throw 'Fixed WebView2 binary hash mismatch.'}
Copy-VerifiedTree (Join-Path $script:ProjectRoot '.tools\webview2-fixed\Microsoft.WebView2.FixedVersionRuntime.153.0.4234.32.x64') (Join-Path $appRoot 'runtime\webview2')
$shell=Get-ScriptShell
$null=Invoke-LoggedProcess $shell @('-NoProfile','-ExecutionPolicy','Bypass','-File',"$PSScriptRoot\Verify-Startup.ps1",'-AppRoot',$appRoot,'-EvidenceRoot',(Join-Path $releaseRoot 'startup-verification')) (Join-Path $releaseRoot 'logs\startup-verification')
$null=Invoke-LoggedProcess $shell @('-NoProfile','-ExecutionPolicy','Bypass','-File',"$PSScriptRoot\Write-Sbom.ps1",'-AppRoot',$appRoot) (Join-Path $releaseRoot 'logs\sbom')
Write-JsonFile ([ordered]@{schemaVersion=1;dataDirectory='data';mode='portable'}) (Join-Path $appRoot 'portable.json')
Copy-Item -LiteralPath (Join-Path $script:ProjectRoot 'installer\README-candidate.md') -Destination (Join-Path $appRoot 'README.md')
$worker=Join-Path $appRoot 'workers\FolderLens.Media.Worker.exe'
$probeDirectory=Join-Path $releaseRoot 'capability-probe'
$probe=Invoke-LoggedProcess $worker @('capabilities',$probeDirectory) (Join-Path $releaseRoot 'logs\media-provider-probe') -AllowFailure
$runtimeCapabilities=$null
if(Test-Path -LiteralPath (Join-Path $probeDirectory 'capabilities.json')){$runtimeCapabilities=Get-Content -LiteralPath (Join-Path $probeDirectory 'capabilities.json') -Raw | ConvertFrom-Json}
$capability=[ordered]@{buildId=$id;workerSha256=(Get-FileHash -LiteralPath $worker).Hash;providerLoadStatus=$(if($probe.exitCode -eq 0){'PASS'}else{'FAIL'});runtimeCapabilities=$runtimeCapabilities;providerProbe=$(Get-Content -LiteralPath $probe.stdout -Raw);formatMatrix='NOT_RUN for this exact candidate';realARW='NOT_RUN for this exact candidate';cleanOfflineWebView2='NOT_RUN';note='Basic diagnostic fixtures do not prove all advertised formats. Missing samples remain NOT_RUN. Earlier build results are not inherited.'}
Write-JsonFile $capability (Join-Path $appRoot 'CAPABILITIES.json')
$after=Get-BuildInputs
if($inputs.sha256 -ne $after.sha256){throw "Source changed during publish; candidate incomplete and not certifiable: $releaseRoot"}
Write-JsonFile ([ordered]@{buildId=$id;source=$after;runtime=$Runtime;configuration=$Configuration;status='CANDIDATE_UNVERIFIED';releaseVerdict='NO-GO';builtUtc=[DateTime]::UtcNow.ToString('o')}) (Join-Path $appRoot 'BUILD-INFO.json')
$manifest=[ordered]@{schemaVersion=1;buildId=$id;sourceSha256=$after.sha256;status='CANDIDATE_UNVERIFIED';releaseVerdict='NO-GO';sourceGitHead=$after.gitHead;selfContainedDotNet=$true;selfContainedWindowsAppSdk=$true;requiredFiles=@('FolderLens.App.exe','FolderLens.App.dll','FolderLens.App.runtimeconfig.json','coreclr.dll','hostfxr.dll','hostpolicy.dll','Microsoft.UI.Xaml.dll','Microsoft.Graphics.Canvas.dll','WebView2Loader.dll','workers/FolderLens.Media.Worker.exe','workers/FolderLens.RawBridge.dll','workers/libraw.dll','workers/libvips-42.dll','workers/magick/policy.xml','content-worker/FolderLens.Content.Worker.exe','native/ffmpeg/ffmpeg.exe','native/ffmpeg/ffprobe.exe','runtime/webview2/msedgewebview2.exe','SBOM.json','THIRD-PARTY-NOTICES.md','BUILD-INFO.json','CAPABILITIES.json','portable.json');files=Get-ReleaseFiles $appRoot;gates=@{cleanOfflinePortable='NOT_RUN';cleanOfflineInstaller='NOT_RUN';upgradeCancelUninstall='NOT_RUN';allP1Acceptance='NOT_RUN';referenceHardwarePerformance='NOT_RUN';sourceLicenseObligations='BLOCKED'}}
if(Test-Path -LiteralPath $scan){$manifest.requiredFiles+=@('scan-worker/FolderLens.Scan.Worker.exe','scan-worker/FolderLens.Scan.Worker.dll','scan-worker/FolderLens.Scan.Worker.deps.json','scan-worker/FolderLens.Scan.Worker.runtimeconfig.json')}
$manifest.requiredFiles+=@('App.xbf','MainWindow.xbf','FolderLens.App.pri')
Write-JsonFile $manifest (Join-Path $releaseRoot 'release-manifest.json')
Write-JsonFile ([ordered]@{artifactRoot=$releaseRoot;buildId=$id}) (Join-Path $script:ProjectRoot 'artifacts\publish\latest-candidate.json')
Write-Host "Candidate published: $releaseRoot. Full release verdict: NO-GO."
if($probe.exitCode -ne 0){throw 'Published media provider failed to load; candidate manifest records FAIL.'}
