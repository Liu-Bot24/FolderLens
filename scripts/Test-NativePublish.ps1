[CmdletBinding()]
param()
. "$PSScriptRoot\Common.ps1"
$actualRoot=$script:ProjectRoot
$fixture=Join-Path $actualRoot ('artifacts\native-publish-test-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts'),(Join-Path $fixture 'src\FolderLens.RawBridge') | Out-Null
foreach($file in @('Common.ps1','Build-Native.ps1','Invoke-SupervisedProcess.ps1','LoggedProcessJob.cs')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $fixture 'scripts')}
$cmake=@'
cmake_minimum_required(VERSION 3.24)
project(NativePublishFixture LANGUAGES CXX)
add_library(FolderLens.RawBridge SHARED fixture.cpp)
add_library(libraw SHARED fixture.cpp)
'@
[IO.File]::WriteAllText((Join-Path $fixture 'src\FolderLens.RawBridge\CMakeLists.txt'),$cmake)
$source=Join-Path $fixture 'src\FolderLens.RawBridge\fixture.cpp'
$script:ProjectRoot=$fixture
try {
    [IO.File]::WriteAllText($source,'extern "C" __declspec(dllexport) int fixture() { return 1; }')
    $first=Build-PublishNativeRuntime (Join-Path $fixture 'artifacts\native-build')
    $oldHash=(Get-FileHash -LiteralPath (Join-Path $first 'FolderLens.RawBridge.dll')).Hash
    $timestamp=(Get-Item -LiteralPath $source).LastWriteTimeUtc
    [IO.File]::WriteAllText($source,'extern "C" __declspec(dllexport) int fixture() { return 2; }')
    (Get-Item -LiteralPath $source).LastWriteTimeUtc=$timestamp
    # Legacy outputs still exist and have the old timestamp. Publishing must use
    # the current native source even when an incremental build could miss it.
    $second=Build-PublishNativeRuntime (Join-Path $fixture 'candidate\native-build')
    $newHash=(Get-FileHash -LiteralPath (Join-Path $second 'FolderLens.RawBridge.dll')).Hash
    if($newHash -eq $oldHash){throw 'Changed native source was not rebuilt.'}
    $rejected=$false
    try{$null=Build-PublishNativeRuntime (Join-Path $fixture 'artifacts\native-build')}catch{$rejected=$_.Exception.Message -like '*must be new*'}
    if(-not $rejected){throw 'Existing native output directory was accepted.'}
    [IO.File]::WriteAllText($source,'this is not valid C++')
    $failed=$false
    try{$null=Build-PublishNativeRuntime (Join-Path $fixture 'invalid\native-build')}catch{$failed=$true}
    if(-not $failed){throw 'A failed native build was accepted.'}
    Write-JsonFile @{status='PASS';changedSourceRebuilt=$true;existingOutputRejected=$true;failedBuildRejected=$true;oldHash=$oldHash;newHash=$newHash} (Join-Path $fixture 'result.json')
    Write-Host "PASS: native publish provenance regression. Evidence: $fixture"
} finally {$script:ProjectRoot=$actualRoot}
