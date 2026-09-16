[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release',[string]$BuildDirectory='artifacts\native-build',[switch]$NoStage,[switch]$Help)
if($Help){Write-Output 'Build-Native.ps1 [-Configuration Debug|Release]: build C ABI RawBridge against pinned local LibRaw.';exit 0}
. "$PSScriptRoot\Common.ps1"
$cmake=Get-CMake
$BuildDirectory=Resolve-ProjectPath $BuildDirectory
$log='artifacts\logs\native-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$null=Invoke-LoggedProcess $cmake @('-S',(Join-Path $script:ProjectRoot 'src\FolderLens.RawBridge'),'-B',$BuildDirectory,'-G','Visual Studio 17 2022','-A','x64') "$log\configure"
$null=Invoke-LoggedProcess $cmake @('--build',$BuildDirectory,'--config',$Configuration,'--clean-first') "$log\build"
if($NoStage){Write-Host "Native bridge rebuilt in $BuildDirectory.";return}
$destination=Join-Path $script:ProjectRoot "src\FolderLens.Media.Worker\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64"
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach($file in @('FolderLens.RawBridge.dll','libraw.dll')){Copy-Item -LiteralPath (Join-Path $BuildDirectory "$Configuration\$file") -Destination $destination}
Copy-Item -LiteralPath (Join-Path $script:ProjectRoot 'native\ffmpeg\ffmpeg.exe') -Destination $destination -Force
Write-Host 'Native bridge and LibRaw copied to media worker build directory.'
