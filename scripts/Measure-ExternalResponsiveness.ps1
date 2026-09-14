[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppRoot,[Parameter(Mandatory=$true)][string]$DataDirectory,[Parameter(Mandatory=$true)][string]$EvidenceRoot)
. "$PSScriptRoot\Common.ps1"
$AppRoot=Resolve-ProjectPath $AppRoot
$DataDirectory=Resolve-ProjectPath $DataDirectory
$EvidenceRoot=Resolve-ProjectPath $EvidenceRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a new evidence directory.'}
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$inputPath=Join-Path $EvidenceRoot 'probe-input.bin'
[IO.File]::WriteAllBytes($inputPath,(New-Object byte[] 65536))
Add-Type -Path (Join-Path $PSScriptRoot 'ResponsivenessProbe.cs')
$probe=New-Object FolderLensResponsivenessProbe $inputPath
try {
 Start-Sleep -Seconds 5
 $probe.Mark('application-startup-and-background-work')
 $run=Invoke-LoggedProcess (Join-Path $AppRoot 'FolderLens.App.exe') @('--verify-refresh','--verify-deployed','--verify-startup-profile','--data-dir',$DataDirectory) (Join-Path $EvidenceRoot 'application') -TimeoutSeconds 240 -AllowFailure
 $probe.Mark('after-application-exit')
 Start-Sleep -Seconds 3
 $samples=$probe.Finish()
 $summaries=@($samples | Group-Object Phase | ForEach-Object {
  $gaps=@($_.Group.GapMs | Sort-Object);$reads=@($_.Group.ReadMs | Sort-Object)
  [pscustomobject]@{phase=$_.Name;samples=$gaps.Count;gapP50Ms=$gaps[[int][Math]::Floor(($gaps.Count-1)*0.5)];gapP95Ms=$gaps[[int][Math]::Floor(($gaps.Count-1)*0.95)];gapMaxMs=$gaps[-1];cachedReadP95Ms=$reads[[int][Math]::Floor(($reads.Count-1)*0.95)];cachedReadMaxMs=$reads[-1]}
 })
 Write-JsonFile @{observerPid=$PID;applicationExitCode=$run.exitCode;phases=$summaries;scope='Independent normal-priority process scheduling and cached 64 KiB read latency. Offscreen native application with generated source fixture. Not voice, Present, cold-disk throughput, DPC/ISR, or ETW CPU Ready measurement.'} (Join-Path $EvidenceRoot 'summary.json')
 $samples | Export-Csv -LiteralPath (Join-Path $EvidenceRoot 'samples.csv') -NoTypeInformation
 $summaries | Format-Table
 if($run.exitCode -ne 0){throw 'Application profile failed; observer samples preserved.'}
} finally {$probe.Dispose()}
