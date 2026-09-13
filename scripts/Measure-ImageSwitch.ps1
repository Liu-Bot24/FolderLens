[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$AppRoot,
 [Parameter(Mandatory=$true)][string]$Source,
 [Parameter(Mandatory=$true)][string]$EvidenceRoot,
 [ValidateSet('Prepared','Encoded','Cold')][string]$Mode='Prepared'
)
. "$PSScriptRoot\Common.ps1"
$AppRoot=Resolve-ProjectPath $AppRoot
$Source=[IO.Path]::GetFullPath($Source).TrimEnd('\')
$EvidenceRoot=Resolve-ProjectPath $EvidenceRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a new evidence directory.'}
if($EvidenceRoot -eq $Source -or $EvidenceRoot.StartsWith($Source+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must be outside the read-only source.'}
if(-not(Test-Path -LiteralPath $Source -PathType Container)){throw 'Source directory does not exist.'}
$identity=@{app=(Get-FileHash -LiteralPath (Join-Path $AppRoot 'FolderLens.App.dll')).Hash;media=(Get-FileHash -LiteralPath (Join-Path $AppRoot 'workers\FolderLens.Media.Worker.dll')).Hash}
$arguments=@('--verify-refresh','--verify-deployed','--verify-wide','--diagnostic-ui','--verify-image-switch',$Source,'--data-dir',$EvidenceRoot)
if($Mode -eq 'Encoded'){$arguments+='--verify-encoded-prefetch'}
if($Mode -eq 'Cold'){$arguments+='--verify-cold-switch'}
$run=Invoke-LoggedProcess (Join-Path $AppRoot 'FolderLens.App.exe') $arguments ($EvidenceRoot+'-run') -TimeoutSeconds 300 -AllowFailure
$report=Get-Content -LiteralPath (Join-Path $EvidenceRoot 'native-refresh.json') -Raw | ConvertFrom-Json
if($run.exitCode -ne 0 -or $report.status -ne 'PASS'){throw 'Image switch measurement failed; inspect the native report.'}
$groups=@()
foreach($group in $report.samples | Group-Object phase){
 $times=@($group.Group.targetDrawMs | Sort-Object)
 $groups+=@{phase=$group.Name;count=$times.Count;p50=$times[[Math]::Ceiling($times.Count*.5)-1];p95=$times[[Math]::Ceiling($times.Count*.95)-1];p99=$times[[Math]::Ceiling($times.Count*.99)-1];max=$times[-1];cacheMisses=@($group.Group | Where-Object {'prefetchMiss' -in $_.stages.name}).Count}
}
$summary=@{status=$report.status;mode=$Mode;binarySha256=$identity;viewport=$report.viewport;groups=$groups;resources=$report.resources;elapsedMs=$report.elapsedMs;scope='111 target CanvasControl Draw samples, not display Present; OS cache not cleared. Cold disables application prefetch. Run without concurrent builds, hashing or other benchmarks.'}
Write-JsonFile $summary (Join-Path $EvidenceRoot 'summary.json')
$groups | ConvertTo-Json
