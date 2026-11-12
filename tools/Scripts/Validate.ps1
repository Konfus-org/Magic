<#
.SYNOPSIS
Runs the checks a change must pass: a clean build, the tests, every sample headless, and every sample with a window,
ending in a screenshot of each to look at.

.DESCRIPTION
The steps run in order and the first failure ends the run with that step's exit code; a run that passes exits 0.
The build is incremental, so what is built already is reused, and the native libraries are built once per machine,
shared by every checkout (docs/GEMS.md, Native libraries). The build makes every warning but a TODO's an
error, so a clean build is one that exits 0. It logs with MSBuild's plain console logger, so the native libraries'
progress shows as it happens, which the terminal logger holds back until each project ends. Formatting is not checked
here: tools/Scripts/Format.ps1 formats a change before it is validated, and the build fails on every rule it applies that a
build can see.
The tests run against that build, so running them alone (-Only Test) needs one. The Headless step runs every project at
once without a window under --fail-on-error, each waiting for its world before it types exit, and
passes when each ends with 0; an error logged during a run exits 2, a run still going after -Seconds is killed and
fails, and a failed run's last output is printed. The Screenshot step runs
tools/Scripts/Screenshot.ps1 on each project and prints the images' paths last: open each and look at what it shows
before reporting the change as working. It needs a display and a GPU; the others do not. Runs from any current
directory.

.PARAMETER Configuration
The build configuration: Debug, Optimize or Release.

.PARAMETER Seconds
The longest each sample run may last, in seconds, before it is killed and fails: a ceiling, not a wait.

.PARAMETER Project
The .magic files the sample runs open, relative to the repository root; every sample's by default.

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
    [int] $Seconds = 300,
    [string[]] $Project = @(),
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
if ($Project.Count -eq 0) {
    $Project = @(Get-ChildItem (Join-Path $root 'samples') -Directory | ForEach-Object { Get-ChildItem $_.FullName -Filter '*.magic' } | Sort-Object FullName | ForEach-Object { $_.FullName })
    if ($Project.Count -eq 0) {
        Write-Output 'There are no projects under samples/ to run.'
        exit 1
    }
}
$screenshots = @()

# Starts every project headless at once, each waiting for its world before it types exit, then waits
# for each to end. Each run's output goes to a file, which is printed only when that run fails. A run still going after
# -Seconds is killed.
function Invoke-Headless {
    & (Join-Path $PSScriptRoot 'Profiling.ps1') -Wait
    $commands = Join-Path ([System.IO.Path]::GetTempPath()) ('Validate-' + [guid]::NewGuid() + '.txt')
    Set-Content -Path $commands -Value @('wait loaded', 'exit')
    $runs = foreach ($one in $Project) {
        $projectPath = if ([System.IO.Path]::IsPathRooted($one)) { $one } else { Join-Path $root $one }
        $log = Join-Path ([System.IO.Path]::GetTempPath()) ('Validate-' + [guid]::NewGuid() + '.log')
        $process = Start-Process -FilePath $magic -ArgumentList "open `"$projectPath`" --headless true --fail-on-error" `
            -NoNewWindow -PassThru -RedirectStandardInput $commands -RedirectStandardOutput $log
        # Reading the handle keeps the exit code readable after the process ends.
        $null = $process.Handle
        [pscustomobject]@{ Project = $projectPath; Process = $process; Log = $log }
    }

    $code = 0
    $started = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($run in $runs) {
        $left = [math]::Max(0, $Seconds * 1000 - $started.ElapsedMilliseconds)
        $result = 0
        if (-not $run.Process.WaitForExit([int]$left)) {
            $run.Process.Kill()
            Write-Output "FAILED: $($run.Project) was still going after $Seconds s."
            $result = 1
        } elseif ($run.Process.ExitCode -ne 0) {
            Write-Output "FAILED: $($run.Project) exited $($run.Process.ExitCode)."
            $result = $run.Process.ExitCode
        }
        if ($result -ne 0) {
            Get-Content $run.Log -Tail 60 -ErrorAction SilentlyContinue
            if ($code -eq 0) { $code = $result }
        }
        Remove-Item $run.Log -ErrorAction SilentlyContinue
    }
    Remove-Item $commands -ErrorAction SilentlyContinue
    $global:LASTEXITCODE = $code
}

# Runs tools/Scripts/Screenshot.ps1 on each project and keeps the images' paths, its output.
function Invoke-Screenshot {
    foreach ($one in $Project) {
        $output = & (Join-Path $PSScriptRoot 'Screenshot.ps1') -Configuration $Configuration -Seconds $Seconds -Project $one
        $code = $LASTEXITCODE
        if ($code -ne 0) {
            $global:LASTEXITCODE = $code
            return
        }

        $script:screenshots += @($output)
    }

    $global:LASTEXITCODE = 0
}

$steps = @(
    @{ Name = 'Build'; Run = { dotnet build $solution --configuration $Configuration -tl:off -p:CSharpier_Check=true } }
    @{ Name = 'Test'; Run = { dotnet test --solution $solution --configuration $Configuration --no-build } }
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

    # Another branch's profiling turn must not be disturbed: wait for it to end before each step.
    & (Join-Path $PSScriptRoot 'Profiling.ps1') -Wait
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
