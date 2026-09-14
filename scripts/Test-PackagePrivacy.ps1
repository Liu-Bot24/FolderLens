[CmdletBinding()]
param()
. "$PSScriptRoot\Common.ps1"
$installer=Get-Content -LiteralPath (Join-Path $script:ProjectRoot 'installer\FolderLens.iss') -Raw
if($installer -match 'recursesubdirs' -or $installer -notmatch '#include SourceFileList'){throw 'Installer still collects a directory instead of the verified manifest.'}
$root=Join-Path $script:ProjectRoot 'artifacts\package-privacy-check'
New-Item -ItemType Directory -Force -Path (Join-Path $root 'data'),(Join-Path $root 'runtime') | Out-Null
[IO.File]::WriteAllText((Join-Path $root 'runtime\component.dll'),'synthetic runtime')
[IO.File]::WriteAllText((Join-Path $root 'data\catalog.sqlite'),'synthetic private data')
function Entry([string]$path){$file=Get-Item -LiteralPath (Join-Path $root $path);[pscustomobject]@{path=$path;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}}
$public=Entry 'runtime/component.dll'
$files=@(Get-PackageFiles $root ([pscustomobject]@{files=@($public)}))
if($files.Count -ne 1 -or $files[0].path -ne $public.path){throw 'Unlisted data entered the package.'}
$lines=@(Get-InnoPackageFileLines $files)
if($lines.Count -ne 1 -or $lines[0] -match 'recursesubdirs|\*|catalog|data\\'){throw 'Installer list includes recursive or private inputs.'}
$failures=0
foreach($bad in @((Entry 'data/catalog.sqlite'),[pscustomobject]@{path='../outside.dll';bytes=1;sha256='bad'},[pscustomobject]@{path='runtime/*.dll';bytes=1;sha256='bad'},[pscustomobject]@{path='catalog.sqlite.pre-v7.bak';bytes=1;sha256='bad'},[pscustomobject]@{path='runtime/component.dll';bytes=1;sha256='bad'})){
 try{$null=@(Get-PackageFiles $root ([pscustomobject]@{files=@($bad)}))}catch{$failures++;continue}
 throw 'Unsafe or changed package input was accepted.'
}
try{$null=@(Get-PackageFiles $root ([pscustomobject]@{files=@($public,$public)}))}catch{$failures++}
if($failures -ne 6){throw 'Duplicate manifest entry was accepted.'}
$installer=Get-Content -LiteralPath (Join-Path $script:ProjectRoot 'installer\FolderLens.iss') -Raw
if($installer -match 'recursesubdirs' -or $installer -notmatch '#include SourceFileList'){throw 'Installer still collects a directory instead of the verified manifest.'}
Write-Host 'PASS: runtime allowlist; private data, traversal, wildcard, backup, changed file and duplicate rejected; installer uses the same explicit list.'
