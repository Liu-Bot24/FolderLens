[CmdletBinding()]
param([switch]$Help)
if($Help){Write-Output 'Check-Environment.ps1: read-only tool inspection; writes artifacts/environment.json and .md, installs nothing, returns nonzero for required missing tools.';exit 0}
. "$PSScriptRoot\Common.ps1"
$checks=@();$dotnet=$null;$sdkVersion=$null
try{$dotnet=Get-DotNet;$sdkVersion=(& $dotnet --version | Out-String).Trim();if($LASTEXITCODE -ne 0){throw 'dotnet --version failed'};$required=(Get-Content (Join-Path $script:ProjectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version;$checks+=@{name='dotnet-sdk';status=$(if($sdkVersion -eq $required){'PASS'}else{'FAIL'});actual=$sdkVersion;required=$required}}catch{$checks+=@{name='dotnet-sdk';status='BLOCKED';reason=$_.Exception.Message}}
$paths=@{
'libraw-sdk'=Join-Path $script:ProjectRoot '.tools\libraw\LibRaw-0.22.2\lib\libraw.lib'
'ffmpeg'=Join-Path $script:ProjectRoot 'native\ffmpeg\ffmpeg.exe'
'ffprobe'=Join-Path $script:ProjectRoot 'native\ffmpeg\ffprobe.exe'
'inno'=Join-Path $script:ProjectRoot '.tools\inno\ISCC.exe'
'fixed-webview2'=Join-Path $script:ProjectRoot '.tools\webview2-fixed\Microsoft.WebView2.FixedVersionRuntime.153.0.4234.32.x64\msedgewebview2.exe'
}
foreach($key in $paths.Keys){$checks+=@{name=$key;status=$(if(Test-Path -LiteralPath $paths[$key]){'PASS'}else{'BLOCKED'});path=$paths[$key]}}
try{$tool=Get-CMake;$checks+=@{name='msvc-cmake';status='PASS';path=$tool}}catch{$checks+=@{name='msvc-cmake';status='BLOCKED';reason=$_.Exception.Message}}
try{$lock=Get-Content (Join-Path $script:ProjectRoot 'native\dependency-lock.json') -Raw | ConvertFrom-Json;$crt=@($lock.components | Where-Object {$_.id -eq 'Microsoft.VC143.CRT'})[0];$directory=Get-CrtDirectory $crt;$checks+=@{name='vc-runtime';status='PASS';path=$directory}}catch{$checks+=@{name='vc-runtime';status='BLOCKED';reason=$_.Exception.Message}}
$kits='C:\Program Files (x86)\Windows Kits\10\bin'
$sdk=@();if(Test-Path -LiteralPath $kits){$sdk=@(Get-ChildItem -LiteralPath $kits -Directory | Where-Object {$_.Name -match '^10\.'} | ForEach-Object {$_.Name})}
$checks+=@{name='windows-sdk';status=$(if($sdk.Count){'PASS'}else{'BLOCKED'});versions=$sdk}
$report=[ordered]@{utc=[DateTime]::UtcNow.ToString('o');os=[Environment]::OSVersion.VersionString;architecture=$env:PROCESSOR_ARCHITECTURE;logicalProcessors=[Environment]::ProcessorCount;powerShell=$PSVersionTable.PSVersion.ToString();checks=$checks;scope='Presence/version inspection only; Build.ps1 performs actual compiler/link validation.'}
$base=Join-Path $script:ProjectRoot 'artifacts\environment'
Write-JsonFile $report ($base+'.json')
$lines=@('# FolderLens build environment', '', $report.utc, '', '| Check | Status |','|---|---|')
$lines+=@($checks | ForEach-Object {'| '+$_.name+' | '+$_.status+' |'})
$lines+=@('', $report.scope)
[IO.File]::WriteAllLines($base+'.md',$lines,(New-Object Text.UTF8Encoding($false)))
$checks | ForEach-Object {[pscustomobject]$_} | Format-Table name,status | Out-Host
if(@($checks | Where-Object {$_.status -ne 'PASS'}).Count){exit 1}
