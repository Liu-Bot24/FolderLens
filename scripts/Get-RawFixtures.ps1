[CmdletBinding()]
param([switch]$Help)
if($Help){'Get-RawFixtures.ps1: downloads the pinned CC0 RAW samples to fixtures/public and verifies SHA-256; existing mismatches are preserved and rejected.';exit 0}
. "$PSScriptRoot\Common.ps1"
$manifest=Get-Content (Join-Path $script:ProjectRoot 'planning\raw-fixtures.json') -Raw | ConvertFrom-Json
$fixtureRoot=[IO.Path]::GetFullPath((Join-Path $script:ProjectRoot 'fixtures\public'))
$results=@()
foreach($fixture in $manifest.fixtures){
 $uri=[uri]$fixture.source
 if($fixture.license -ne 'CC0-1.0' -or $uri.Scheme -ne 'https' -or $uri.Host -ne 'raw.pixls.us' -or $uri.AbsolutePath -notmatch '^/getfile.php/[0-9]+/nice/' -or $fixture.sha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Invalid fixture origin or checksum.'}
 $target=[IO.Path]::GetFullPath((Join-Path $fixtureRoot $fixture.relativePath))
 if(-not $target.StartsWith($fixtureRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture path escapes its directory.'}
 if(Test-Path -LiteralPath $target){if((Get-FileHash -LiteralPath $target).Hash -ne $fixture.sha256){throw "Existing fixture checksum mismatch: $($fixture.fixtureId)"}}
 else{
  New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
  $partial=$target+'.'+[guid]::NewGuid().ToString('N')+'.part'
  Invoke-WebRequest -Uri $uri -OutFile $partial -TimeoutSec 120
  if((Get-FileHash -LiteralPath $partial).Hash -ne $fixture.sha256){throw "Downloaded fixture checksum mismatch: $($fixture.fixtureId); partial retained."}
  Move-Item -LiteralPath $partial -Destination $target
 }
 $length=(Get-Item -LiteralPath $target).Length
 $results+=[ordered]@{fixtureId=$fixture.fixtureId;relativePath=$fixture.relativePath;bytes=$length;sha256=$fixture.sha256;status='VERIFIED'}
 Write-Host "$($fixture.fixtureId) $($fixture.family): $length bytes verified"
 Write-JsonFile ([ordered]@{checkedUtc=[DateTime]::UtcNow.ToString('o');fixtures=$results}) (Join-Path $script:ProjectRoot 'artifacts\m0\raw-family\downloads.json')
}
