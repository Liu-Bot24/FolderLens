# Shared Windows PowerShell 5.1 / PowerShell 7 helpers. No global policy changes.
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:ProjectRoot = Split-Path $PSScriptRoot -Parent
function Write-JsonFile($Value, [string]$Path) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Path -Parent) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 40), (New-Object Text.UTF8Encoding($false)))
}
function Resolve-ProjectPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $script:ProjectRoot $Path))
}
function Get-DotNet {
    $local = Join-Path $script:ProjectRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) { $tool = $local } else { $tool = (Get-Command dotnet -ErrorAction Stop).Source }
    $env:DOTNET_ROOT = Split-Path $tool -Parent
    $env:DOTNET_CLI_HOME = Join-Path $script:ProjectRoot '.tools\cli-home'
    $env:NUGET_PACKAGES = Join-Path $script:ProjectRoot '.nuget\packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    return $tool
}
function Get-ScriptShell {
    # Keep nested scripts on the host whose module paths are inherited by child
    # processes. Mixing pwsh's environment with Windows PowerShell breaks cmdlets.
    $name=if($PSVersionTable.PSEdition -eq 'Core'){'pwsh.exe'}else{'powershell.exe'}
    return Join-Path $PSHOME $name
}
function Get-VisualStudioRoots {
    if($env:FOLDERLENS_VS_ROOT){
        if(-not(Test-Path -LiteralPath $env:FOLDERLENS_VS_ROOT -PathType Container)){throw 'FOLDERLENS_VS_ROOT does not exist.'}
        [IO.Path]::GetFullPath($env:FOLDERLENS_VS_ROOT)
    }
    $locator=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if(Test-Path -LiteralPath $locator){
        $found=@(& $locator -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
        if($LASTEXITCODE -ne 0){throw 'Visual Studio discovery failed.'}
        $found | Where-Object {$_ -and (Test-Path -LiteralPath $_ -PathType Container)}
    }
}
function Get-CMake {
    if($env:FOLDERLENS_CMAKE){
        if(-not(Test-Path -LiteralPath $env:FOLDERLENS_CMAKE -PathType Leaf)){throw 'FOLDERLENS_CMAKE does not name an existing executable.'}
        return [IO.Path]::GetFullPath($env:FOLDERLENS_CMAKE)
    }
    $command=Get-Command cmake.exe -ErrorAction SilentlyContinue
    if($command){return $command.Source}
    foreach($root in @(Get-VisualStudioRoots)){
        $candidate=Join-Path $root 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
        if(Test-Path -LiteralPath $candidate -PathType Leaf){return $candidate}
    }
    throw 'CMake was not found on PATH or in Visual Studio. Set FOLDERLENS_CMAKE to its executable.'
}
function Get-CrtDirectory($Component) {
    $candidates=@()
    if($env:FOLDERLENS_CRT_DIR){$candidates=@($env:FOLDERLENS_CRT_DIR)}
    else {
        foreach($root in @(Get-VisualStudioRoots)){$candidates+=Join-Path $root ("VC\Redist\MSVC\"+$Component.version+'\x64\Microsoft.VC143.CRT')}
        if($Component.PSObject.Properties['redistributableDirectory']){$candidates+=$Component.redistributableDirectory}
    }
    foreach($candidate in $candidates | Select-Object -Unique){
        if(-not(Test-Path -LiteralPath $candidate -PathType Container)){continue}
        foreach($file in $Component.files){
            $inputFile=Join-Path $candidate $file.file
            if(-not(Test-Path -LiteralPath $inputFile -PathType Leaf)){throw "Locked CRT input is missing: $inputFile"}
            if((Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash -ne $file.sha256){throw "Locked CRT hash mismatch: $($file.file)"}
        }
        return [IO.Path]::GetFullPath($candidate)
    }
    throw "VC runtime $($Component.version) was not found. Set FOLDERLENS_CRT_DIR to the directory containing the hash-locked DLLs."
}
function ConvertTo-NativeArgument([string]$Value) {
    if ($Value.IndexOf([char]0) -ge 0) { throw 'NUL is not valid in a process argument.' }
    # CRT quoting, including backslashes before quotes and at the end. No shell.
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Invoke-LoggedProcess {
    param([string]$FilePath, [string[]]$Arguments = @(), [string]$LogBase,
          [string]$WorkingDirectory = $script:ProjectRoot, [switch]$AllowFailure,
          [ValidateRange(1,7200)][int]$TimeoutSeconds=1800)
    if (-not $LogBase) { throw 'A persistent log path is required.' }
    $LogBase = Resolve-ProjectPath $LogBase
    New-Item -ItemType Directory -Force -Path (Split-Path $LogBase -Parent) | Out-Null
    $start = New-Object Diagnostics.ProcessStartInfo
    if(-not ('FolderLensLoggedProcessJob' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'LoggedProcessJob.cs')}
    $job=New-Object FolderLensLoggedProcessJob
    $commandArguments=($Arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
    $start.FileName = Get-ScriptShell
    $start.Arguments = (@('-NoProfile','-File',(Join-Path $PSScriptRoot 'Invoke-SupervisedProcess.ps1')) | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    $start.EnvironmentVariables.Clear()
    foreach ($entry in [Environment]::GetEnvironmentVariables().GetEnumerator()) {
        $start.EnvironmentVariables[$entry.Key.ToUpperInvariant()] = $entry.Value
    }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $began = [DateTime]::UtcNow
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $outFile = [IO.File]::Create($LogBase + '.stdout.log')
    $errFile = [IO.File]::Create($LogBase + '.stderr.log')
    $code=-1; $peak=0; $timedOut=$false; $failure=$null; $started=$false; $stdout=$null; $stderr=$null
    try {
        if (-not $process.Start()) { throw "Could not start $FilePath" }
        $started=$true
        $job.Assign($process)
        $stdout = $process.StandardOutput.BaseStream.CopyToAsync($outFile)
        $stderr = $process.StandardError.BaseStream.CopyToAsync($errFile)
        $configuration=@{executable=$FilePath;arguments=$commandArguments;workingDirectory=$WorkingDirectory} | ConvertTo-Json -Compress
        $process.StandardInput.Write($configuration)
        $process.StandardInput.Close()
        if(-not $process.WaitForExit($TimeoutSeconds*1000)) {
            $timedOut=$true
            $job.Terminate()
            if(-not $process.WaitForExit(5000)){throw 'Supervised process did not exit after job termination.'}
        }
        $peak = $process.PeakWorkingSet64
        $drained=[Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout,$stderr))
        if(-not $drained.Wait(10000)) { $timedOut=$true; throw 'Process output did not close within the drain deadline.' }
        $code = if($timedOut){124}else{$process.ExitCode}
    } catch {
        $failure=$_.Exception.ToString();$code=if($timedOut){124}else{1}
        if($started) {
            try {
                $job.Terminate()
                if(-not $process.WaitForExit(5000)){throw 'Launcher did not exit after job termination.'}
                if($stdout -and $stderr){
                    $drained=[Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout,$stderr))
                    if(-not $drained.Wait(5000)){throw 'Output remained open after job termination.'}
                }
            }
            catch { $failure+="`nProcess tree cleanup failed: "+$_.Exception.ToString() }
        }
    } finally { $job.Dispose(); $outFile.Dispose(); $errFile.Dispose(); $process.Dispose() }
    $watch.Stop()
    $result = [ordered]@{ executable=$FilePath; arguments=$Arguments; workingDirectory=$WorkingDirectory;
        startedUtc=$began.ToString('o'); elapsedMs=$watch.Elapsed.TotalMilliseconds; exitCode=$code;
        launcherPeakWorkingSetBytes=$peak; stdout=$LogBase+'.stdout.log'; stderr=$LogBase+'.stderr.log';
        timeoutSeconds=$TimeoutSeconds; timedOut=$timedOut; failure=$failure }
    Write-JsonFile $result ($LogBase + '.command.json')
    Write-Host ("{0}: exit {1}; logs {2}.*" -f [IO.Path]::GetFileName($FilePath),$code,$LogBase)
    if ($code -ne 0 -and -not $AllowFailure) {
        Get-Content -LiteralPath ($LogBase+'.stderr.log') -Tail 30 | Write-Host
        Get-Content -LiteralPath ($LogBase+'.stdout.log') -Tail 30 | Write-Host
        throw "Process failed with exit code ${code}: $FilePath. Original logs: $LogBase.*"
    }
    return [pscustomobject]$result
}
function Stop-LoggedProcessTree([Diagnostics.Process]$Process) {
    if($Process.HasExited){return}
    if($PSVersionTable.PSEdition -eq 'Core') { $Process.Kill($true) }
    else {
        $killer=New-Object Diagnostics.Process
        $killer.StartInfo=New-Object Diagnostics.ProcessStartInfo
        $killer.StartInfo.FileName=Join-Path ([Environment]::SystemDirectory) 'taskkill.exe'
        $killer.StartInfo.Arguments='/PID '+$Process.Id+' /T /F'
        $killer.StartInfo.UseShellExecute=$false;$killer.StartInfo.CreateNoWindow=$true
        try {
            if(-not $killer.Start()){throw 'Could not start process-tree cleanup.'}
            if(-not $killer.WaitForExit(5000)){$killer.Kill();throw 'Process-tree cleanup timed out.'}
            if($killer.ExitCode -ne 0 -and -not $Process.HasExited){throw "Process-tree cleanup failed: $($killer.ExitCode)"}
        } finally {$killer.Dispose()}
    }
    if(-not $Process.WaitForExit(5000)){throw 'Child process did not exit after tree termination.'}
}
function Get-BuildInputs {
    $files = @()
    foreach ($folder in @('src','tests','scripts','installer','contracts','native','assets')) {
        $path = Join-Path $script:ProjectRoot $folder
        if (Test-Path -LiteralPath $path) {
            $files += Get-ChildItem -LiteralPath $path -Recurse -File | Where-Object {
                $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Name -notin @('AGENTS.md','AGENTS.override.md','PACKAGING_EVIDENCE.md')
            }
        }
    }
    $files += Get-ChildItem -LiteralPath $script:ProjectRoot -File | Where-Object { $_.Name -match '^(global\.json|Directory\..*\.props|NuGet\.Config|FolderLens\.slnx|build-release\.cmd)$' }
    $records = @($files | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ path=$_.FullName.Substring($script:ProjectRoot.Length+1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $bytes = [Text.Encoding]::UTF8.GetBytes((($records | ForEach-Object { $_.path+' '+$_.sha256 }) -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','') } finally { $sha.Dispose() }
    $gitHead = & git -c "safe.directory=$($script:ProjectRoot.Replace('\','/'))" -C $script:ProjectRoot rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { $gitHead='UNCOMMITTED' }
    return [pscustomobject]@{ sha256=$digest; gitHead=[string]$gitHead; files=$records }
}
function Build-PublishNativeRuntime([string]$BuildDirectory,[string]$Configuration='Release') {
    $BuildDirectory=Resolve-ProjectPath $BuildDirectory
    if(Test-Path -LiteralPath $BuildDirectory){throw 'A publish native build directory must be new; refusing stale outputs.'}
    $buildScript=Join-Path $script:ProjectRoot 'scripts\Build-Native.ps1'
    $null=Invoke-LoggedProcess (Get-ScriptShell) @('-NoProfile','-File',$buildScript,'-Configuration',$Configuration,'-BuildDirectory',$BuildDirectory,'-NoStage') ($BuildDirectory+'-log')
    $runtime=Join-Path $BuildDirectory $Configuration
    foreach($file in @('FolderLens.RawBridge.dll','libraw.dll')){
        if(-not(Test-Path -LiteralPath (Join-Path $runtime $file) -PathType Leaf)){throw "Candidate native output missing: $file"}
    }
    return $runtime
}
function Copy-VerifiedTree([string]$Source, [string]$Destination) {
    $Source=[IO.Path]::GetFullPath($Source).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Missing input directory: $Source" }
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        if ($file.Extension -eq '.pdb') { continue }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing linked release input: $($file.FullName)" }
        $target=Join-Path $Destination $file.FullName.Substring($Source.Length+1)
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Conflicting publish files: $target" }
        } else {
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
}
function Get-ReleaseFiles([string]$Directory) {
    $Directory=[IO.Path]::GetFullPath($Directory).TrimEnd('\')
    return @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ path=$_.FullName.Substring($Directory.Length+1).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
function Get-RequiredWorkerFiles {
    foreach($worker in @('scan-worker/FolderLens.Scan.Worker','content-worker/FolderLens.Content.Worker','workers/FolderLens.Media.Worker')) {
        foreach($extension in @('.exe','.dll','.deps.json','.runtimeconfig.json')) { $worker+$extension }
    }
}
function Test-PrivateReleasePath([string]$Path) {
    $normalized=$Path.Replace('\','/')
    $name=($normalized -split '/')[-1]
    return ($normalized -match '(^|/)(data|fixtures|\.git|\.nuget|obj|bin)/' -or
        $name -match '\.(pdb|user)$|\.(sqlite|sqlite3|db)(-(wal|shm|journal))?$|\.bak($|[-.])')
}
function Get-PackageFiles([string]$AppRoot, $Manifest) {
    $AppRoot=[IO.Path]::GetFullPath($AppRoot).TrimEnd('\')
    $seen=@{}
    if(@($Manifest.files).Count -eq 0){throw 'Package manifest is empty.'}
    foreach($file in $Manifest.files){
        $relative=([string]$file.path).Replace('\','/')
        if([string]::IsNullOrWhiteSpace($relative) -or $relative -match '[\x00-\x1f:*?"{};]|^/|(^|/)(\.|\.\.)(/|$)|//|/$' -or (Test-PrivateReleasePath $relative)){
            throw 'Private or unsafe package manifest path.'
        }
        if($seen.ContainsKey($relative)){throw 'Duplicate package manifest path.'}
        $seen[$relative]=$true
        $full=Join-Path $AppRoot $relative
        # Check every path component: a linked parent also escapes the package root.
        $cursor=$AppRoot
        foreach($part in @('')+@($relative -split '/')){
            if($part){$cursor=Join-Path $cursor $part}
            $item=Get-Item -LiteralPath $cursor -Force
            if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Linked package input is not allowed.'}
        }
        if($item.PSIsContainer -or $item.Length -ne $file.bytes -or (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ne $file.sha256){throw 'Package input differs from its manifest.'}
        [pscustomobject]@{path=$relative;fullPath=$full;bytes=$file.bytes;sha256=$file.sha256}
    }
}
function Get-InnoPackageFileLines($Files) {
    foreach($file in $Files){
        if($file.path -eq 'portable.json'){continue}
        $relative=$file.path.Replace('/','\')
        $parent=Split-Path $relative -Parent
        $destination='{app}\versions\{#BuildId}'+$(if($parent){'\'+$parent}else{''})
        'Source: "{#AppSource}\'+$relative+'"; DestDir: "'+$destination+'"; Flags: ignoreversion'
    }
}
