[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
. "$PSScriptRoot\Common.ps1"
$EvidenceRoot=Resolve-ProjectPath $EvidenceRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a new fixture directory.'}
$publish=Join-Path $EvidenceRoot 'publish'
$app=Join-Path $publish 'app'
New-Item -ItemType Directory -Path $app | Out-Null
# Inert files exercise the production validator; these are never executed.
foreach($file in @('App.xbf','MainWindow.xbf','FolderLens.App.pri')+@(Get-RequiredWorkerFiles)){
 $path=Join-Path $app $file
 New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
 [IO.File]::WriteAllText($path,'fixture')
}
Write-JsonFile @{runtimeOptions=@{includedFrameworks=@(@{name='Microsoft.NETCore.App';version='10.0.0'})}} (Join-Path $app 'FolderLens.App.runtimeconfig.json')
Write-JsonFile @{buildId='zip-fixture';workerSha256=(Get-FileHash (Join-Path $app 'workers/FolderLens.Media.Worker.exe')).Hash;providerLoadStatus='PASS'} (Join-Path $app 'CAPABILITIES.json')
Write-JsonFile @{managed=@('fixture');native=@('fixture')} (Join-Path $app 'SBOM.json')
$files=@(Get-ReleaseFiles $app)
$manifest=Join-Path $publish 'release-manifest.json'
Write-JsonFile @{buildId='zip-fixture';requiredFiles=@('App.xbf');files=$files;gates=@{runtime='NOT_RUN'}} $manifest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$results=@()
foreach($case in @('normal','duplicate-replaces-missing','missing','case-collision','duplicate-manifest','missing-manifest','wrong-manifest','outer-hash')){
 $packageRoot=Join-Path $EvidenceRoot $case
 New-Item -ItemType Directory -Path $packageRoot | Out-Null
 $zipPath=Join-Path $packageRoot 'fixture-portable-unverified.zip'
 $zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
 try{
  $entries=@($files | ForEach-Object {[pscustomobject]@{name=('app/'+$_.path);source=(Join-Path $app $_.path)}})
  if($case -eq 'duplicate-replaces-missing'){$entries[1]=$entries[0]}
  if($case -eq 'missing'){$entries=$entries[1..($entries.Count-1)]}
  if($case -eq 'case-collision'){$entries[1]=[pscustomobject]@{name=$entries[0].name.ToUpperInvariant();source=$entries[0].source}}
  if($case -ne 'missing-manifest'){$entries+=[pscustomobject]@{name='release-manifest.json';source=$manifest}}
  if($case -eq 'duplicate-manifest'){$entries[0]=[pscustomobject]@{name='release-manifest.json';source=$manifest}}
  foreach($entry in $entries){
   $content=if($case -eq 'wrong-manifest' -and $entry.name -eq 'release-manifest.json'){[Text.Encoding]::UTF8.GetBytes('{}')}else{[IO.File]::ReadAllBytes($entry.source)}
   $stream=$zip.CreateEntry($entry.name).Open();try{$stream.Write($content,0,$content.Length)}finally{$stream.Dispose()}
  }
 }finally{$zip.Dispose()}
 $hash=(Get-FileHash -LiteralPath $zipPath).Hash
 if($case -eq 'outer-hash'){$hash='0'*64}
 Write-JsonFile @{buildId='zip-fixture';artifactRoot=$publish;files=@(@{file=(Split-Path $zipPath -Leaf);sha256=$hash})} (Join-Path $packageRoot 'package-manifest.json')
 $accepted=$true;$reason=''
 try{& "$PSScriptRoot\Verify-Release.ps1" -ArtifactRoot $packageRoot}catch{$accepted=$false;$reason=$_.Exception.Message}
 $results+=@{case=$case;accepted=$accepted;expectedAccepted=($case -eq 'normal');reason=$reason}
}
Write-JsonFile $results (Join-Path $EvidenceRoot 'results.json')
if(@($results | Where-Object {$_.accepted -ne $_.expectedAccepted}).Count){throw 'Portable ZIP validation regression. See results.json.'}
Write-Host 'PASS: exact ZIP contents, manifest and outer hash (8 cases).'
