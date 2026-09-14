[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$AppRoot,
 [Parameter(Mandatory=$true)][string]$EvidenceRoot,
 [ValidateRange(1,600)][int]$TimeoutSeconds=90
)
. "$PSScriptRoot\Common.ps1"
$AppRoot=Resolve-ProjectPath $AppRoot
$EvidenceRoot=Resolve-ProjectPath $EvidenceRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a new evidence directory for this regression batch.'}
$exe=Join-Path $AppRoot 'FolderLens.App.exe'
if(-not(Test-Path -LiteralPath $exe -PathType Leaf)){throw 'Application entry is missing.'}
$cases=@(
 'scan-audit-r2','scan-audit-r3','scan-audit-r4-empty','scan-audit-r4-results','scan-audit-r4-busy',
 'thumbnail-priority','menu-availability','browser-status','collections','quick-collections','format-choices','filter-panel','navigation-roots','tree-current-folder','wheel-distance','prefetch-adoption','prefetch-turnaround','prepared-cache',
 'fit-lock','press-gesture','filmstrip','viewer-information','selection-appearance','directory-filter',
 'audio-recovery','video-card','keyboard-completion','preview-close','preview-pending-close',
 'root-from-viewer','selection-race','slide-tick-race','slide-error'
)
$reports=@()
$video=Join-Path $EvidenceRoot 'generated-video.mp4'
$null=Invoke-LoggedProcess (Join-Path $AppRoot 'native\ffmpeg\ffmpeg.exe') @('-hide_banner','-loglevel','error','-f','lavfi','-i','testsrc2=size=640x360:rate=24','-t','2','-c:v','libx264','-pix_fmt','yuv420p',$video) (Join-Path $EvidenceRoot 'video-fixture') -TimeoutSeconds 30
foreach($case in $cases){
 $directory=Join-Path $EvidenceRoot $case
 $arguments=@('--verify-refresh','--verify-deployed',('--verify-'+$case))
 if($case -eq 'video-card'){$arguments+=@($video)}
 $arguments+=@('--data-dir',$directory)
 $run=Invoke-LoggedProcess $exe $arguments ($directory+'-run') -TimeoutSeconds $TimeoutSeconds -AllowFailure
 $path=Join-Path $directory 'native-refresh.json'
 $status='FAIL';$errorText=$null
 if(Test-Path -LiteralPath $path){
  try{
   $report=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
   if($report.status -eq 'CLOSE_PENDING'){
    $path=Join-Path $directory 'native-close.json'
    $report=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
   }
   $status=$report.status;if($report.PSObject.Properties['error']){$errorText=$report.error}
  }
  catch{$errorText=$_.Exception.Message}
 }else{$errorText='Native verification report was not produced.'}
 if($run.exitCode -ne 0){$status='FAIL'}
 $reports+=@{case=$case;status=$status;exitCode=$run.exitCode;report=$path;error=$errorText}
 Write-Host "$case : $status"
}
Write-JsonFile @{utc=[DateTime]::UtcNow.ToString('o');appSha256=(Get-FileHash -LiteralPath (Join-Path $AppRoot 'FolderLens.App.dll')).Hash;cases=$reports;scope='Generated local fixtures and native WinUI paths; not full hardware, archive, or installation acceptance.'} (Join-Path $EvidenceRoot 'summary.json')
if(@($reports | Where-Object {$_.status -ne 'PASS'}).Count -gt 0){throw 'One or more native regression cases failed. See summary.json.'}
