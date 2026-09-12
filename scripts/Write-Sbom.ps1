[CmdletBinding()]
param([string]$AppRoot,[switch]$Help)
if($Help){Write-Output 'Write-Sbom.ps1 -AppRoot <published-app-directory>: inventories exact NuGet package locks and local native records, preserves available license texts, records unresolved obligations. Does not clear licenses.';exit 0}
. "$PSScriptRoot\Common.ps1"
$AppRoot=Resolve-ProjectPath $AppRoot
$licenses=Join-Path $AppRoot 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
$components=@();$seen=@{}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $script:ProjectRoot 'src') -Recurse -Filter packages.lock.json -File){
 $lock=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
 foreach($tfm in $lock.dependencies.PSObject.Properties){foreach($package in $tfm.Value.PSObject.Properties){
  if($package.Value.type -eq 'Project'){continue}
  $id=$package.Name;$version=$package.Value.resolved;$key=$id.ToLowerInvariant()+'/'+$version
  if($seen.ContainsKey($key)){continue};$seen[$key]=$true
  $directory=Join-Path $script:ProjectRoot ('.nuget\packages\'+$id.ToLowerInvariant()+'\'+$version)
  $nuspec=Join-Path $directory ($id.ToLowerInvariant()+'.nuspec')
  $license='UNKNOWN';$repository=$null;$notices=@()
  if(Test-Path -LiteralPath $nuspec){
   [xml]$spec=Get-Content -LiteralPath $nuspec -Raw
   $metadata=$spec.SelectSingleNode('//*[local-name()="metadata"]')
   $licenseNode=$metadata.SelectSingleNode('*[local-name()="license"]')
   if($licenseNode){$license=$licenseNode.InnerText}
   $repositoryNode=$metadata.SelectSingleNode('*[local-name()="repository"]')
   if($repositoryNode){$repository=@{url=$repositoryNode.GetAttribute('url');commit=$repositoryNode.GetAttribute('commit')}}
   $destination=Join-Path $licenses ($id+'-'+$version)
   New-Item -ItemType Directory -Force -Path $destination | Out-Null
   Copy-Item -LiteralPath $nuspec -Destination $destination
   foreach($notice in Get-ChildItem -LiteralPath $directory -File -Recurse | Where-Object {$_.Name -match '^(LICENSE|LICENCE|COPYING|COPYRIGHT|NOTICE|THIRD.PARTY)'}){
    $relative=$notice.FullName.Substring($directory.Length+1);$out=Join-Path $destination $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $out -Parent) | Out-Null
    Copy-Item -LiteralPath $notice.FullName -Destination $out
    $notices+='licenses/'+$id+'-'+$version+'/'+$relative.Replace('\','/')
   }
  }
  $components+=[pscustomobject]@{id=$id;version=$version;purl=('pkg:nuget/'+$id+'@'+$version);nugetContentHash=$package.Value.contentHash;license=$license;repository=$repository;notices=$notices;scope='Restored source project dependency; build-only and runtime packages both listed';licenseObligationReview='PENDING'}
 }}
}
$localCopies=@{
'LibRaw'=@('.tools\libraw\LibRaw-0.22.2\COPYRIGHT','.tools\libraw\LibRaw-0.22.2\LICENSE.LGPL','.tools\libraw\LibRaw-0.22.2\LICENSE.CDDL')
'FFmpeg'=@('native\ffmpeg\LICENSE.txt','native\ffmpeg\buildconf.txt')
'InnoSetup'=@('.tools\inno\license.txt')
'DotNet'=@('.tools\dotnet\LICENSE.txt','.tools\dotnet\ThirdPartyNotices.txt')
}
foreach($name in $localCopies.Keys){$dest=Join-Path $licenses $name;New-Item -ItemType Directory -Force -Path $dest | Out-Null;foreach($relative in $localCopies[$name]){Copy-Item -LiteralPath (Join-Path $script:ProjectRoot $relative) -Destination $dest}}
$native=Get-Content (Join-Path $script:ProjectRoot 'native\dependency-lock.json') -Raw | ConvertFrom-Json
$staticVips=Get-Content (Join-Path $script:ProjectRoot '.nuget\packages\netvips.native.win-x64\8.18.6\versions.json') -Raw | ConvertFrom-Json
$sbom=[ordered]@{schema='FolderLens SBOM inventory v1';createdUtc=[DateTime]::UtcNow.ToString('o');architecture='x64';status='INVENTORY_NOT_LICENSE_CLEARANCE';managed=$components;native=$native.components;libvipsStaticDependencyVersions=$staticVips;distributionBlockers=$native.distributionBlockers;noticeCompleteness='Available upstream notices included; some NuGet licenses are expression/URL-only; full text and native corresponding-source obligations must be completed before external distribution.'}
Write-JsonFile $sbom (Join-Path $AppRoot 'SBOM.json')
Copy-Item -LiteralPath (Join-Path $script:ProjectRoot 'native\dependency-lock.json') -Destination (Join-Path $AppRoot 'dependency-lock.json')
Copy-Item -LiteralPath (Join-Path $script:ProjectRoot 'native\THIRD-PARTY-NOTICES.md') -Destination (Join-Path $AppRoot 'THIRD-PARTY-NOTICES.md')
