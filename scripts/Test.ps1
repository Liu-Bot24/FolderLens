[CmdletBinding()]
param([ValidateSet('Unit','Integration','Media','UI','All')][string]$Suite='All',[switch]$Help)
if($Help){Write-Output 'Test.ps1 [-Suite Unit|Integration|Media|UI|All]: run already-built Release tests with TRX and original logs. Build.ps1 must run first. Missing or empty suites fail; All includes real UI.';exit 0}
. "$PSScriptRoot\Common.ps1"
$dotnet=Get-DotNet
$id=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$resultRoot=Join-Path $script:ProjectRoot "artifacts\tests\$id"
New-Item -ItemType Directory -Force -Path $resultRoot | Out-Null
$project='tests\FolderLens.UnitTests\FolderLens.UnitTests.csproj'
# Existing tests share one assembly. These filters state their evidence scope explicitly.
$filters=@{
Unit='FullyQualifiedName~CapacityTests|FullyQualifiedName~NaturalOrder|FullyQualifiedName~ExclusionUsesSegmentBoundary|FullyQualifiedName~GenerationRejectsOldResult|FullyQualifiedName~FrameRejectsOversizedAndUnknownFields|FullyQualifiedName~RejectsInvalidFilterInsteadOfQuerying|FullyQualifiedName~UnsafeLocalResourceIsRejectedBeforeFileAccess'
Integration='FullyQualifiedName!~CapacityTests&FullyQualifiedName!~NaturalOrder&FullyQualifiedName!~ExclusionUsesSegmentBoundary&FullyQualifiedName!~GenerationRejectsOldResult&FullyQualifiedName!~WorkerTests&FullyQualifiedName!~RejectsInvalidFilterInsteadOfQuerying&FullyQualifiedName!~UnsafeLocalResourceIsRejectedBeforeFileAccess'
Media='FullyQualifiedName~ActualWorkerReturnsOwnImageAndRestartsAfterRawCancellation|FullyQualifiedName~ActualAnimationFramesChangeAndStayInOneFile|FullyQualifiedName~ActualMultiVariantFramesAndRequestedTiffPageArePreserved'
}
$requested=if($Suite -eq 'All'){@('Unit','Integration','Media','UI')}else{@($Suite)}
$results=@()
foreach($name in $requested){
 if($name -eq 'UI'){
  $uiProject=Join-Path $script:ProjectRoot 'tests\FolderLens.UiTests\FolderLens.UiTests.csproj'
  if(-not(Test-Path -LiteralPath $uiProject)){$results += [pscustomobject]@{suite=$name;status='NOT_RUN';reason='Dedicated executable WinUI/UIA suite is not present. Manual developer UI evidence is not an automated suite.';total=0};continue}
  $arguments=@('test',$uiProject,'-c','Release','--no-build','--no-restore')
 }else{$arguments=@('test',$project,'-c','Release','--no-build','--no-restore','--filter',$filters[$name])}
 $suiteDirectory=Join-Path $resultRoot $name
 $arguments+=@('--logger',"trx;LogFileName=$name.trx",'--results-directory',$suiteDirectory)
 $run=Invoke-LoggedProcess $dotnet $arguments (Join-Path $suiteDirectory 'test') -AllowFailure
 $trx=Join-Path $suiteDirectory "$name.trx"
 $status='FAIL';$total=0
 if(Test-Path -LiteralPath $trx){
  [xml]$xml=Get-Content -LiteralPath $trx -Raw
  $counters=$xml.TestRun.ResultSummary.Counters;$total=[int]$counters.total
  if($run.exitCode -eq 0 -and $total -gt 0 -and [int]$counters.passed -eq $total){$status='PASS'}
 }
 $results+=[pscustomobject]@{suite=$name;status=$status;total=$total;exitCode=$run.exitCode;trx=$trx}
}
Write-JsonFile ([ordered]@{id=$id;requested=$Suite;source=Get-BuildInputs;suites=$results;scope='Repository automated tests only; not full P1 or clean-machine acceptance.'}) (Join-Path $resultRoot 'summary.json')
$results | Format-Table suite,status,total | Out-Host
if(@($results | Where-Object {$_.status -ne 'PASS'}).Count -gt 0){exit 1}
