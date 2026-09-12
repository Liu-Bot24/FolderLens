[CmdletBinding()]
param([switch]$NonInteractive,[switch]$Help)
if($Help){Write-Output 'Bootstrap.ps1 [-NonInteractive]: restores already-approved local SDK/LibRaw/WebView2 archives only after hash validation. No network, machine-wide installers, or global policy changes. Missing compilers/Inno/FFmpeg require the recorded approved toolchain; exits nonzero with exact gaps.';exit 0}
. "$PSScriptRoot\Common.ps1"
$lockPath=Join-Path $script:ProjectRoot 'native\dependency-lock.json'
if(-not(Test-Path -LiteralPath $lockPath)){throw 'native/dependency-lock.json is missing.'}
$lock=Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
foreach($item in $lock.bootstrap){
 $target=Resolve-ProjectPath $item.presence
 if(Test-Path -LiteralPath $target){continue}
 $archive=Resolve-ProjectPath $item.archive
 if(-not(Test-Path -LiteralPath $archive)){throw "Approved archive missing: $($item.archive). Source: $($item.url). No unapproved download or installation was attempted."}
 if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $item.sha256){throw "Archive hash mismatch: $($item.archive)"}
 $destination=Resolve-ProjectPath $item.destination
 New-Item -ItemType Directory -Force -Path $destination | Out-Null
 if($item.format -eq 'zip'){Add-Type -AssemblyName System.IO.Compression.FileSystem;[IO.Compression.ZipFile]::ExtractToDirectory($archive,$destination)}
 elseif($item.format -eq 'cab'){$expand=Join-Path $env:WINDIR 'System32\expand.exe';$null=Invoke-LoggedProcess $expand @($archive,'-F:*',$destination) ('artifacts\logs\bootstrap-'+$item.id)}
 else{throw "Unsupported approved archive format: $($item.format)"}
 if(-not(Test-Path -LiteralPath $target)){throw "Archive extraction did not create expected file: $target"}
}
$shell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$null=Invoke-LoggedProcess $shell @('-NoProfile','-ExecutionPolicy','Bypass','-File',"$PSScriptRoot\Check-Environment.ps1") 'artifacts\logs\bootstrap-environment'
Write-Host 'Approved local dependencies are available. NuGet restores remain locked in Build.ps1.'
