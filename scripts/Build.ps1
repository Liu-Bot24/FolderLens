[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release',[ValidateSet('Development','Production')][string]$BuildFlavor='Development',[switch]$Help)
if($Help){Write-Output 'Build.ps1 [-Configuration Debug|Release] [-BuildFlavor Development|Production]: locked managed restore/build and CMake RawBridge. Development enables local diagnostics; Production disables them.';exit 0}
. "$PSScriptRoot\Common.ps1"
$id=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$log="artifacts\logs\build-$id"
$inputs=Get-BuildInputs
$dotnet=Get-DotNet
$null=Invoke-LoggedProcess $dotnet @('restore','FolderLens.slnx','--locked-mode') "$log\restore"
$null=Invoke-LoggedProcess $dotnet @('build','FolderLens.slnx','-c',$Configuration,'--no-restore','-p:ContinuousIntegrationBuild=true',('-p:FolderLensBuildFlavor='+$BuildFlavor)) "$log\managed"
$shell=Join-Path $PSHOME 'powershell.exe'
if(-not(Test-Path -LiteralPath $shell)){$shell=Join-Path $PSHOME 'pwsh.exe'}
$null=Invoke-LoggedProcess $shell @('-NoProfile','-ExecutionPolicy','Bypass','-File',"$PSScriptRoot\Build-Native.ps1",'-Configuration',$Configuration) "$log\native"
$null=Invoke-LoggedProcess $shell @('-NoProfile','-ExecutionPolicy','Bypass','-File',"$PSScriptRoot\Stage-BuildRuntime.ps1",'-Configuration',$Configuration) "$log\stage-runtime"
$after=Get-BuildInputs
if($inputs.sha256 -ne $after.sha256){throw 'Source inputs changed during build; no coherent build certificate was written.'}
Write-JsonFile ([ordered]@{id=$id;configuration=$Configuration;buildFlavor=$BuildFlavor;developmentDiagnostics=($BuildFlavor -eq 'Development');source=$after;status='BUILT';acceptance='NOT_RUN';logs=$log}) (Join-Path $script:ProjectRoot "artifacts\build-$Configuration.json")
Write-Host "Managed and native build complete. Build identity: $id. Acceptance remains separate."
