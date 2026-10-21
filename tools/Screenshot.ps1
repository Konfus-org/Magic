<#
.SYNOPSIS
Runs a project with a window until its world has loaded, takes a screenshot through the console, and prints the image's
path.

.DESCRIPTION
Magic runs with --fail-on-error and these console lines piped in: wait loaded, which holds until the world has loaded
however long its shaders take to compile, a wait for what fades in to settle, any -Commands, a short wait for them, then
screenshot and quit. The log is shown as it comes; the script's only output is the path of the PNG, its last line, so a
caller can keep it: $png = tools/Screenshot.ps1. Open the image and look at it: a screenshot that was taken is not one
that shows the change working. The run fails when Magic exits non-zero, when the world never finished loading within
-Frames, or when no screenshot was logged. Needs a display and a GPU, and a build (tools/Validate.ps1 -Only Build).
Runs from any current directory.

.PARAMETER Configuration
The build configuration whose Magic runs: Debug, Optimize or Release.

.PARAMETER Settle
How many frames pass after the world has loaded before the screenshot, for what fades in, streams in or gathers light
over frames to settle.

.PARAMETER Frames
The most frames the run may last: a ceiling, not a wait. A run that has not loaded its world by then fails.

.PARAMETER Project
The project to run: a .magic file or the folder holding one, relative to the repository root.

.PARAMETER Commands
Console lines sent after the world has loaded and settled, before the screenshot, such as one choosing a debug view.
`help` in a run lists them.

.EXAMPLE
tools/Screenshot.ps1

.EXAMPLE
tools/Screenshot.ps1 -Commands 'view Albedo'
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Optimize', 'Release')]
    [string] $Configuration = 'Debug',
    [ValidateRange(0, [int]::MaxValue)]
    [int] $Settle = 600,
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Frames = 20000,
    [string] $Project = 'samples',
    [string[]] $Commands = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false

$root = Split-Path -Parent $PSScriptRoot
$magic = Join-Path (Join-Path (Join-Path (Join-Path $root 'Build') 'bin') $Configuration) 'Magic'
if (Test-Path "$magic.exe") { $magic = "$magic.exe" }
if (-not (Test-Path $magic)) {
    Write-Host "There is no $magic to run: build first (tools/Validate.ps1 -Only Build)."
    exit 1
}
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }

# The screenshot is written by a worker, so the run goes on a few frames before it quits.
$lines = @('wait loaded', "wait $Settle") + $Commands + @('wait 60', 'screenshot', 'wait 60', 'quit')
$log = $lines | & $magic --project $projectPath --frames $Frames --fail-on-error | ForEach-Object { Write-Host $_; $_ }
$code = $LASTEXITCODE

# A line reads "12:34:56.789 Info     f1234 t1 Screenshot: <path> (File.cs:60 Member)".
$found = $log | Select-String -Pattern 'Screenshot: (.+\.png) \(' | Select-Object -Last 1
if ($code -ne 0) {
    Write-Host "FAILED: Magic exited $code"
    exit $code
}
if ($null -eq $found) {
    Write-Host "FAILED: the world never finished loading within $Frames frames, so no screenshot was taken."
    exit 1
}
$path = $found.Matches[0].Groups[1].Value
if (-not (Test-Path $path)) {
    Write-Host "FAILED: the log names $path but there is no such file."
    exit 1
}
Write-Output $path
