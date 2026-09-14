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
$child=New-Object Diagnostics.Process
$child.StartInfo=$start
try {
 if(-not $child.Start()){throw 'Supervised process did not start.'}
 $child.WaitForExit()
 exit $child.ExitCode
} finally {$child.Dispose()}
