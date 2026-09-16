[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppRoot,[Parameter(Mandatory=$true)][string]$EvidenceRoot)
. "$PSScriptRoot\Common.ps1"
$appRoot=Resolve-ProjectPath $AppRoot
$evidence=Resolve-ProjectPath $EvidenceRoot
if(Test-Path -LiteralPath $evidence){throw 'Use a fresh evidence directory.'}
$cases=@('toolbar-widths','preview-layout','filter-editor','group-lazy','range-publication-cost','cache-recovery','demand-layout','pro-scan-closeout','rejected-root-monitor','directory-navigation','long-image-wheel','player-settings','empty-audit','empty-state','theme','first-page','text-reader','markdown-demand','group-collapse','tree-selection-visible','container-recycle','refresh-metadata','retired-row','thumbnail-upgrade','page-mode','tree-page-order','tree-recovery','r2-close-query','r2-close-root','r2-flow','group-publication','visible-group-move','audit-three','active-view-entry','cache-fallback','thumbnail-bounds','collection-contract','native-ranges','group-order','tree-scale','r2-identity','identity-scale','r2-tree-limit')
$reports=@()
foreach($case in $cases){
 $directory=Join-Path $evidence $case
 if($case -eq 'tree-recovery'){$null=Invoke-LoggedProcess (Join-Path $appRoot 'FolderLens.App.exe') @('--verify-refresh','--verify-deployed','--verify-tree-abrupt-exit','--data-dir',$directory) ($directory+'-crash-run') -TimeoutSeconds 30}
 $run=Invoke-LoggedProcess (Join-Path $appRoot 'FolderLens.App.exe') @('--verify-refresh','--verify-deployed',('--verify-'+$case),'--data-dir',$directory) ($directory+'-run') -TimeoutSeconds 120 -AllowFailure
 $path=Join-Path $directory 'native-refresh.json';$status='FAIL';$errorText=$null
 if(Test-Path -LiteralPath $path){
  try{$report=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
   if($report.status -eq 'CLOSE_PENDING'){$path=Join-Path $directory 'native-close.json';$report=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json}
   $status=$report.status;if($report.PSObject.Properties['error']){$errorText=$report.error}
  }catch{$errorText=$_.Exception.Message}
 }else{$errorText='No report produced.'}
 if($run.exitCode -ne 0){$status='FAIL'}
 $reports+=@{case=$case;status=$status;exitCode=$run.exitCode;report=$path;error=$errorText}
 Write-JsonFile @{cases=$reports;appSha256=(Get-FileHash -LiteralPath (Join-Path $appRoot 'FolderLens.App.dll')).Hash} (Join-Path $evidence 'progress.json')
 Write-Host "$case : $status"
}
Write-JsonFile @{cases=$reports;scope='Off-screen native generated-fixture checks, without physical input, clipboard or external application interaction.';appSha256=(Get-FileHash -LiteralPath (Join-Path $appRoot 'FolderLens.App.dll')).Hash} (Join-Path $evidence 'summary.json')
if(@($reports | Where-Object {$_.status -ne 'PASS'}).Count){throw 'Additional native verification failed.'}
