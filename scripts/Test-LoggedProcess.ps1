[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
. "$PSScriptRoot\Common.ps1"
$EvidenceRoot=Resolve-ProjectPath $EvidenceRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a new evidence directory.'}
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$sleeper=Join-Path $EvidenceRoot 'sleeper.ps1'
@'
param([string]$PidFile)
$shell=Join-Path $PSHOME $(if($PSVersionTable.PSEdition -eq 'Core'){'pwsh.exe'}else{'powershell.exe'})
$child=Start-Process -FilePath $shell -ArgumentList '-NoProfile -Command Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru
[IO.File]::WriteAllText($PidFile,($child.Id.ToString()))
Write-Output 'parent-started'
Start-Sleep -Seconds 60
'@ | Set-Content -LiteralPath $sleeper -Encoding UTF8
$pidFile=Join-Path $EvidenceRoot 'child.pid'
$result=Invoke-LoggedProcess (Get-ScriptShell) @('-NoProfile','-File',$sleeper,$pidFile) (Join-Path $EvidenceRoot 'timeout') -TimeoutSeconds 3 -AllowFailure
if(-not $result.timedOut -or $result.exitCode -ne 124){throw 'Sleeper was not reported as a timeout.'}
if(-not(Test-Path $pidFile)){throw 'Fixture child was not started.'}
$childId=[int](Get-Content -LiteralPath $pidFile)
if(Get-Process -Id $childId -ErrorAction SilentlyContinue){throw 'Timed-out child process survived tree cleanup.'}
if(-not ([string](Get-Content -LiteralPath $result.stdout -Raw) -match 'parent-started')){throw 'Output was not preserved.'}
$normal=Invoke-LoggedProcess (Get-ScriptShell) @('-NoProfile','-Command','Write-Output normal; exit 0') (Join-Path $EvidenceRoot 'normal') -TimeoutSeconds 5
if($normal.exitCode -ne 0 -or $normal.timedOut){throw 'Successful execution was misclassified.'}
if(-not ([string](Get-Content -LiteralPath $normal.stdout -Raw) -match 'normal')){throw 'Successful command output was lost.'}
$failed=Invoke-LoggedProcess (Get-ScriptShell) @('-NoProfile','-Command','[Console]::Out.WriteLine("capture-stdout"); [Console]::Error.WriteLine("capture-stderr"); exit 7') (Join-Path $EvidenceRoot 'failed') -TimeoutSeconds 5 -AllowFailure
if($failed.exitCode -ne 7 -or $failed.timedOut){throw 'Ordinary nonzero exit was misclassified.'}
if(-not ([string](Get-Content -LiteralPath $failed.stdout -Raw) -match 'capture-stdout') -or -not ([string](Get-Content -LiteralPath $failed.stderr -Raw) -match 'capture-stderr')){throw 'Failed command stdout/stderr was lost.'}
Write-Host 'PASS: timeout evidence, process-tree cleanup, output retention and normal/nonzero exit.'
