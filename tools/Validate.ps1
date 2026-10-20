<#
.SYNOPSIS
Runs the checks a change must pass: a clean build, the tests, and the samples headless and with a window.

.DESCRIPTION
The steps run in order and the first failure ends the run with that step's exit code; a run that passes exits 0.
The build is incremental, so what is built already is reused, and a linked worktree builds against the native
libraries its main checkout has built, unless MagicNativeLibs says otherwise: building those again takes far longer
than everything else here. The build already treats every warning as an error, so a clean build is one that exits 0.
The tests run against that build, so running them alone (-Only Test) needs one. The sample runs pass
--fail-on-error, so an error logged during a run exits 2. The Window step needs a display and a GPU; the others do
not. Runs from any current directory.

.PARAMETER Configuration
The build configuration: Debug, Optimize or Release.

.PARAMETER Frames
How many frames each sample run lasts.

.PARAMETER Project
The project the sample runs open: a .magic file or the folder holding one, relative to the repository root.

.PARAMETER Only
The steps to run when not all of them: Build, Test, Headless, Window, in that order whatever order they are given.

.EXAMPLE
tools/Validate.ps1

.EXAMPLE
tools/Validate.ps1 -Only Build,Headless
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Optimize', 'Release')]
    [string] $Configuration = 'Debug',
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Frames = 120,
    [string] $Project = 'samples',
    [ValidateSet('Build', 'Test', 'Headless', 'Window')]
    [string[]] $Only = @('Build', 'Test', 'Headless', 'Window')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false
# The build's worker processes end with the build instead of lingering, one per core, for a quarter of an hour.
$env:MSBUILDDISABLENODEREUSE = '1'

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'Magic.slnx'

# A linked worktree reuses the native libraries its main checkout has built, in place: a build there only touches
# them when a library's pinned commit differs, so one checkout at a time should build.
if (-not $env:MagicNativeLibs) {
    $common = git -C $root rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($LASTEXITCODE -eq 0 -and $common) {
        $mainRoot = Split-Path -Parent $common
        $natives = Join-Path (Join-Path $mainRoot 'Build') 'NativeLibs'
        if ($mainRoot -ne $root -and (Test-Path $natives)) {
            $env:MagicNativeLibs = Join-Path $natives ''
        }
    }
}
$magic = Join-Path (Join-Path (Join-Path (Join-Path $root 'Build') 'bin') $Configuration) 'Magic'
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }

$steps = @(
    @{ Name = 'Build'; Run = { dotnet build $solution --configuration $Configuration } }
    @{ Name = 'Test'; Run = { dotnet test $solution --configuration $Configuration --no-build } }
    @{ Name = 'Headless'; Run = { & $magic --project $projectPath --frames $Frames --fail-on-error --headless } }
    @{ Name = 'Window'; Run = { & $magic --project $projectPath --frames $Frames --fail-on-error } }
)

$code = 0
$failed = $null
$summary = @()
foreach ($step in $steps) {
    if ($Only -notcontains $step.Name) { continue }
    if ($step.Name -ne 'Build' -and $step.Name -ne 'Test') {
        # Named here rather than at the start: on Windows it is Magic.exe, which the Build step may have just made.
        if (Test-Path "$magic.exe") { $magic = "$magic.exe" }
        if (-not (Test-Path $magic)) {
            Write-Output "There is no $magic to run: build first (tools/Validate.ps1 -Only Build)."
            exit 1
        }
    }

    Write-Output ""
    Write-Output "==> $($step.Name)"
    $started = [System.Diagnostics.Stopwatch]::StartNew()
    & $step.Run
    $code = $LASTEXITCODE
    $summary += '{0,-9} exit {1} in {2:n0} s' -f $step.Name, $code, $started.Elapsed.TotalSeconds
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
exit $code
