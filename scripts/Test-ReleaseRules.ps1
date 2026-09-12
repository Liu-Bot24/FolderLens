[CmdletBinding()]
param()
. "$PSScriptRoot\Common.ps1"
$cases=@{
 'licenses/Microsoft.Data.Sqlite-10.0.12/microsoft.data.sqlite.nuspec'=$false
 'workers/SQLitePCLRaw.core.dll'=$false
 'runtime/webview2/icudtl.dat'=$false
 'catalog.sqlite'=$true
 'catalog.sqlite-wal'=$true
 'nested/catalog.sqlite-shm'=$true
 'nested/catalog.db-journal'=$true
 'data/settings.json'=$true
 'nested\fixtures\photo.jpg'=$true
 'FolderLens.App.pdb'=$true
}
foreach($item in $cases.GetEnumerator()){
 if((Test-PrivateReleasePath $item.Key) -ne $item.Value){throw "Release path classification failed: $($item.Key)"}
}
if(-not(Test-Path -LiteralPath (Get-ScriptShell))){throw 'Current script shell is missing.'}
Write-Host "PASS: $($cases.Count) release path cases; current script host exists."
