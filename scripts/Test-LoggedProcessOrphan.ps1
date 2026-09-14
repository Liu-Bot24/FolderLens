[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
. "$PSScriptRoot\Common.ps1"
$EvidenceRoot=Resolve-ProjectPath $EvidenceRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a new evidence directory.'}
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$fixture=Join-Path $EvidenceRoot 'parent.ps1'
@'
param([string]$PidFile)
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=Join-Path $PSHOME 'pwsh.exe'
$start.Arguments='-NoProfile -Command "Write-Output child-started; Start-Sleep -Seconds 60"'
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$child=[Diagnostics.Process]::Start($start)
[IO.File]::WriteAllText($PidFile,$child.Id.ToString())
Write-Output 'parent-exiting'
exit 0
'@ | Set-Content -LiteralPath $fixture -Encoding UTF8
$pidFile=Join-Path $EvidenceRoot 'child.pid'
try {
 $result=Invoke-LoggedProcess (Get-ScriptShell) @('-NoProfile','-File',$fixture,$pidFile) (Join-Path $EvidenceRoot 'orphan') -TimeoutSeconds 5 -AllowFailure
 if(-not(Test-Path -LiteralPath $pidFile)){throw 'Child fixture did not start.'}
 $fixtureChild=[int](Get-Content -LiteralPath $pidFile)
 if(Get-Process -Id $fixtureChild -ErrorAction SilentlyContinue){throw 'Exited parent left its pipe-holding child alive.'}
 if(-not $result.timedOut -or $result.exitCode -ne 124){throw 'Inherited pipe deadline was not reported as a timeout.'}
 if((Get-Content -LiteralPath $result.stdout -Raw) -notmatch 'parent-exiting'){throw 'Parent output was lost.'}
 Write-Host 'PASS: exited parent, inherited output, descendant cleanup, bounded failure.'
} finally {
 # This PID was created exclusively by this generated fixture, in this run.
 if(Test-Path -LiteralPath $pidFile){
  $fixtureChild=[int](Get-Content -LiteralPath $pidFile)
  $remaining=Get-CimInstance Win32_Process -Filter "ProcessId=$fixtureChild"
  if($remaining -and $remaining.CommandLine -like '*Write-Output child-started; Start-Sleep -Seconds 60*'){Stop-Process -Id $fixtureChild -Force}
 }
}
