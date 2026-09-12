[CmdletBinding()]
param([ValidateSet('All','Database','Text1GiB','Text5GiB')][string]$Scenario='All',[string]$FixtureRoot,[switch]$Help)
if($Help){Write-Output 'Benchmark.ps1 -Scenario All|Database|Text1GiB|Text5GiB -FixtureRoot <local-output-directory>. Creates actual text (up to 6 GiB) plus synthetic million-row SQLite; uses built Diagnostics and emits raw CSV/JSON. No UI, cold OS cache, or million-file enumeration claim.';exit 0}
. "$PSScriptRoot\Common.ps1"
if(-not $FixtureRoot){throw '-FixtureRoot is required; choose a local test directory with at least 8 GiB available for All.'}
$FixtureRoot=Resolve-ProjectPath $FixtureRoot
if($FixtureRoot.StartsWith('\\')){throw 'Fixtures and SQLite must be on a local volume.'}
$exe=Join-Path $script:ProjectRoot 'src\FolderLens.Diagnostics\bin\Release\net10.0-windows10.0.26100.0\FolderLens.Diagnostics.exe'
if(-not(Test-Path -LiteralPath $exe)){throw 'Diagnostics is not built; run Build.ps1 first.'}
$null=Get-DotNet
$runs=@()
$wanted=if($Scenario -eq 'All'){@('Database','Text1GiB','Text5GiB')}else{@($Scenario)}
foreach($name in $wanted){
 $directory=Join-Path $FixtureRoot ($name+'-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
 $arguments=if($name -eq 'Database'){@('database',$directory,'1000000')}elseif($name -eq 'Text1GiB'){@('text',$directory,'1')}else{@('text',$directory,'5')}
 $run=Invoke-LoggedProcess $exe $arguments (Join-Path $directory 'benchmark') -AllowFailure
 $runs+=[pscustomobject]@{scenario=$name;exitCode=$run.exitCode;directory=$directory;result=(Join-Path $directory 'result.json')}
}
Write-JsonFile ([ordered]@{utc=[DateTime]::UtcNow.ToString('o');runs=$runs;source=Get-BuildInputs;referenceHardware='NOT_RUN';uiPerformance='NOT_RUN';coldOSCache='NOT_RUN'}) (Join-Path $FixtureRoot 'benchmark-summary.json')
if(@($runs | Where-Object {$_.exitCode -ne 0}).Count -gt 0){exit 1}
