<#
.SYNOPSIS
Runs the checks a change must pass: a clean build, a format check, the tests, the samples headless until their world
has loaded, and the samples with a window until their world has loaded, ending in a screenshot to look at.

.DESCRIPTION
The steps run in order and the first failure ends the run with that step's exit code; a run that passes exits 0.
The build is incremental, so what is built already is reused, and the native libraries are built once per machine,
shared by every checkout (src/Bundled/Gems/README.md, Native libraries). The build already treats every warning as an
error, so a clean build is one that exits 0. It logs with MSBuild's plain console logger, so the native libraries'
progress shows as it happens, which the terminal logger holds back until each project ends.
The Format step checks the whole tree against .editorconfig with dotnet format and writes nothing: it fails on what
tools/Format.ps1 would change, what a build cannot check among it, such as a file's line endings. It reads the
restore and the generated code of the configuration built, so running it alone (-Only Format) needs a build.
The tests run against that build, so running them alone (-Only Test) needs one. Both sample runs pass --fail-on-error,
so an error logged during a run exits 2, and both wait until the world has loaded (the console's wait loaded), shaders
compiled and the domain open, so a world that never loads fails rather than passing on the loading screen. The
Screenshot step runs tools/Screenshot.ps1 and prints the image's path last: open it and look at what it shows before
reporting the change as working. It needs a display and a GPU; the others do not. Runs from any current directory.

.PARAMETER Configuration
The build configuration: Debug, Optimize or Release.

.PARAMETER Frames
The most frames each sample run may last before it counts as never having loaded: a ceiling, not a wait.

.PARAMETER Project
The project the sample runs open: a .magic file or the folder holding one, relative to the repository root.

.PARAMETER Only
The steps to run when not all of them: Build, Format, Test, Headless, Screenshot, in that order whatever order they are
given.

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
    [int] $Frames = 20000,
    [string] $Project = 'samples',
    [ValidateSet('Build', 'Format', 'Test', 'Headless', 'Screenshot')]
    [string[]] $Only = @('Build', 'Format', 'Test', 'Headless', 'Screenshot')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false
# The build's worker processes end with the build instead of lingering, one per core, for a quarter of an hour.
$env:MSBUILDDISABLENODEREUSE = '1'

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'Magic.slnx'
$magic = Join-Path (Join-Path (Join-Path (Join-Path $root 'Build') 'bin') $Configuration) 'Magic'
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }
$screenshot = $null

# Checks the whole tree and writes nothing. dotnet format takes no MSBuild properties, so the configuration built
# reaches its project load through the environment, where MSBuild reads it, and the caller's value is put back after.
function Invoke-FormatCheck {
    $saved = $env:Configuration
    $env:Configuration = $Configuration
    try {
        dotnet format $solution --verify-no-changes --no-restore
    } finally {
        $env:Configuration = $saved
    }
}

# Runs the project headless until its world has loaded, then quits; a run that reaches -Frames first never loaded.
function Invoke-Headless {
    $log = @('wait loaded', 'quit') | & $magic --project $projectPath --frames $Frames --fail-on-error --headless | ForEach-Object { Write-Host $_; $_ }
    $code = $LASTEXITCODE
    if ($code -eq 0 -and -not ($log | Select-String -SimpleMatch 'quit typed in the terminal')) {
        Write-Host "FAILED: the world never finished loading within $Frames frames."
        $code = 1
    }
    $global:LASTEXITCODE = $code
}

# Runs tools/Screenshot.ps1 and keeps the image's path, its last line of output.
function Invoke-Screenshot {
    $output = & (Join-Path $PSScriptRoot 'Screenshot.ps1') -Configuration $Configuration -Frames $Frames -Project $Project
    $code = $LASTEXITCODE
    if ($code -eq 0) { $script:screenshot = @($output)[-1] }
    $global:LASTEXITCODE = $code
}

$steps = @(
    @{ Name = 'Build'; Run = { dotnet build $solution --configuration $Configuration -tl:off } }
    @{ Name = 'Format'; Run = { Invoke-FormatCheck } }
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
            Write-Output "There is no $magic to run: build first (tools/Validate.ps1 -Only Build)."
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
if ($null -ne $screenshot) {
    Write-Output ""
    Write-Output "Screenshot: $screenshot"
    Write-Output 'Open it and look at what it shows before reporting the change as working.'
}
exit $code
