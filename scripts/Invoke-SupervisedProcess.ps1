# stdin is released by the supervisor only after this launcher joins its job.
# No shell evaluation of the requested command or its arguments.
$ErrorActionPreference='Stop'
$configuration=[Console]::In.ReadToEnd() | ConvertFrom-Json
if(-not $configuration){throw 'Missing supervised command.'}
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=$configuration.executable
$start.Arguments=$configuration.arguments
$start.WorkingDirectory=$configuration.workingDirectory
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$child=New-Object Diagnostics.Process
$child.StartInfo=$start
try {
 if(-not $child.Start()){throw 'Supervised process did not start.'}
 # A hidden Windows child does not reliably inherit its launcher's redirected
 # standard handles. Forward both streams explicitly and concurrently, including
 # nonzero exits; the outer job still bounds descendants holding these pipes.
 $stdout=$child.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardOutput())
 $stderr=$child.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError())
 $child.WaitForExit()
 [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout,$stderr)).GetAwaiter().GetResult()
 exit $child.ExitCode
} finally {$child.Dispose()}
