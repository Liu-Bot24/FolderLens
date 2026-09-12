[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release',[string]$AppRoot,[switch]$Help)
if($Help){Write-Output 'Run-Local.ps1 [-Configuration Debug|Release] [-AppRoot <published-app-directory>]: starts an existing WinUI build. Does not build, download, or install.';exit 0}
. "$PSScriptRoot\Common.ps1"
if(-not $AppRoot){$AppRoot="src\FolderLens.App\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64"}
$AppRoot=Resolve-ProjectPath $AppRoot
$exe=Join-Path $AppRoot 'FolderLens.App.exe'
if(-not(Test-Path -LiteralPath $exe)){throw "Application is not built: $exe"}
# A visible window is the explicit purpose of this command.
$process=Start-Process -FilePath $exe -WorkingDirectory $AppRoot -PassThru
Write-Host "Started FolderLens (PID $($process.Id)); this is not UI acceptance."
