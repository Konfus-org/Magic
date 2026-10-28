<#
.SYNOPSIS
Runs the checks a change must pass: a clean build, the tests, the sample headless, and the sample with a window, ending
in a screenshot to look at.

.DESCRIPTION
The steps run in order and the first failure ends the run with that step's exit code; a run that passes exits 0.
The build is incremental, so what is built already is reused, and the native libraries are built once per machine,
shared by every checkout (src/Bundled/Gems/README.md, Native libraries). The build makes every warning but a TODO's an
error, so a clean build is one that exits 0. It logs with MSBuild's plain console logger, so the native libraries'
progress shows as it happens, which the terminal logger holds back until each project ends. Formatting is not checked
here: tools/Scripts/Format.ps1 formats a change before it is validated, and the build fails on every rule it applies that a
build can see.
The tests run against that build, so running them alone (-Only Test) needs one. The Headless step runs the project
without a window under --fail-on-error, types exit into it and passes when it ends with 0; an error logged during the
run exits 2, and a run still going after -Seconds is killed and fails. The Screenshot step runs
tools/Scripts/Screenshot.ps1 on the project and prints the images' paths last: open each and look at what it shows
before reporting the change as working. It needs a display and a GPU; the others do not. Runs from any current
directory.

.PARAMETER Configuration
The build configuration: Debug, Optimize or Release.

.PARAMETER Seconds
The longest each sample run may last, in seconds, before it is killed and fails: a ceiling, not a wait.

.PARAMETER Project
The .magic file the sample runs open, relative to the repository root.

.PARAMETER Only
The steps to run when not all of them: Build, Test, Headless, Screenshot, in that order whatever order they are given.

.EXAMPLE
tools/Scripts/Validate.ps1

.EXAMPLE
tools/Scripts/Validate.ps1 -Only Build,Headless
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Optimize', 'Release')]
    [string] $Configuration = 'Debug',
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Seconds = 120,
    [string] $Project = 'samples/HelloWorld/HelloWorld.magic',
    [ValidateSet('Build', 'Test', 'Headless', 'Screenshot')]
    [string[]] $Only = @('Build', 'Test', 'Headless', 'Screenshot')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false
# The build's worker processes end with the build instead of lingering, one per core, for a quarter of an hour.
$env:MSBUILDDISABLENODEREUSE = '1'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$solution = Join-Path (Join-Path $root 'src') 'Magic.slnx'
$magic = Join-Path (Join-Path (Join-Path (Join-Path $root 'Build') 'bin') $Configuration) 'Magic'
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }
$screenshots = @()

# Runs the project headless, types exit into it and waits for it to end; a run still going after -Seconds is killed.
function Invoke-Headless {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $magic
    $info.Arguments = "--project `"$projectPath`" --headless true --fail-on-error"
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $process = [System.Diagnostics.Process]::Start($info)
    $process.StandardInput.WriteLine('exit')
    $process.StandardInput.Close()
    if (-not $process.WaitForExit($Seconds * 1000)) {
        $process.Kill()
        Write-Host "FAILED: the run was still going after $Seconds s."
        $global:LASTEXITCODE = 1
        return
    }
    $global:LASTEXITCODE = $process.ExitCode
}

# Runs tools/Scripts/Screenshot.ps1 on the project and keeps the images' paths, its output.
function Invoke-Screenshot {
    $output = & (Join-Path $PSScriptRoot 'Screenshot.ps1') -Configuration $Configuration -Seconds $Seconds -Project $Project
    $code = $LASTEXITCODE
    if ($code -eq 0) { $script:screenshots = @($output) }
    $global:LASTEXITCODE = $code
}

$steps = @(
    @{ Name = 'Build'; Run = { dotnet build $solution --configuration $Configuration -tl:off -p:CSharpier_Check=true } }
    @{ Name = 'Test'; Run = { dotnet test $solution --configuration $Configuration --no-build } }
    @{ Name = 'Headless'; Run = { Invoke-Headless } }
    @{ Name = 'Screenshot'; Run = { Invoke-Screenshot } }
)

$code = 0
$failed = $null
$summary = @()
foreach ($step in $steps) {
    if ($Only -notcontains $step.Name) { continue }
    if ($step.Name -in 'Headless', 'Screenshot') {
        # Named here rather than at the start: on Windows it is Magic.exe, which the Build step may have just made.
        if (Test-Path "$magic.exe") { $magic = "$magic.exe" }
        if (-not (Test-Path $magic)) {
            Write-Output "There is no $magic to run: build first (tools/Scripts/Validate.ps1 -Only Build)."
            exit 1
        }
    }

    Write-Output ""
    Write-Output "==> $($step.Name)"
    $started = [System.Diagnostics.Stopwatch]::StartNew()
    & $step.Run
    $code = $LASTEXITCODE
    $summary += '{0,-10} exit {1} in {2:n0} s' -f $step.Name, $code, $started.Elapsed.TotalSeconds
    if ($code -ne 0) {
        $failed = $step.Name
        break
    }
}

Write-Output ""
foreach ($line in $summary) { Write-Output $line }
if ($null -eq $failed) {
    Write-Output 'PASSED'
} else {
    Write-Output "FAILED: $failed exited $code"
}
if ($screenshots.Count -gt 0) {
    Write-Output ""
    foreach ($path in $screenshots) { Write-Output "Screenshot: $path" }
    Write-Output 'Open each and look at what it shows before reporting the change as working.'
}
exit $code
