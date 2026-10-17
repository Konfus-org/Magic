<#
.SYNOPSIS
Runs a project with a window, takes a screenshot through the console, and prints the image's path.

.DESCRIPTION
Magic runs with --fail-on-error and these console lines piped in: a wait for the scene to be drawn, any -Commands,
a short wait for them to settle, then screenshot. The log is shown as it comes; the script's only output is the
path of the PNG, its last line, so a caller can keep it: $png = tools/Screenshot.ps1. The run fails when Magic exits
non-zero or no screenshot was logged. Needs a display and a GPU, and a build (tools/Validate.ps1 -Only Build).
Runs from any current directory.

.PARAMETER Configuration
The build configuration whose Magic runs: Debug, Optimize or Release.

.PARAMETER Wait
How many frames pass before the screenshot is taken. The first frames go by before the scene is drawn, so this
stays well past them.

.PARAMETER Frames
How many frames the run lasts; it must reach past the wait, the settle and the screenshot.

.PARAMETER Project
The project to run: a .magic file or the folder holding one, relative to the repository root.

.PARAMETER Commands
Console lines sent after the wait and before the screenshot, such as one choosing a debug view. `help` in a run
lists them.

.EXAMPLE
tools/Screenshot.ps1

.EXAMPLE
tools/Screenshot.ps1 -Commands 'view Albedo'
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Optimize', 'Release')]
    [string] $Configuration = 'Debug',
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Wait = 1500,
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Frames = 2000,
    [string] $Project = 'samples',
    [string[]] $Commands = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false

$root = Split-Path -Parent $PSScriptRoot
$magic = Join-Path (Join-Path (Join-Path (Join-Path $root 'Build') 'net10.0') $Configuration) 'bin'
$magic = Join-Path $magic 'Magic'
if (Test-Path "$magic.exe") { $magic = "$magic.exe" }
if (-not (Test-Path $magic)) {
    Write-Host "There is no $magic to run: build first (tools/Validate.ps1 -Only Build)."
    exit 1
}
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }

$settle = 400
$lines = @("wait $Wait") + $Commands + @("wait $settle", 'screenshot')
$log = $lines | & $magic --project $projectPath --frames $Frames --fail-on-error | ForEach-Object { Write-Host $_; $_ }
$code = $LASTEXITCODE

# A line reads "12:34:56.789 Info     f1234 t1 Screenshot: <path> (File.cs:60 Member)".
$found = $log | Select-String -Pattern 'Screenshot: (.+\.png) \(' | Select-Object -Last 1
if ($code -ne 0) {
    Write-Host "FAILED: Magic exited $code"
    exit $code
}
if ($null -eq $found) {
    Write-Host "FAILED: no screenshot was logged; -Frames ($Frames) must reach past -Wait ($Wait) plus $settle."
    exit 1
}
$path = $found.Matches[0].Groups[1].Value
if (-not (Test-Path $path)) {
    Write-Host "FAILED: the log names $path but there is no such file."
    exit 1
}
Write-Output $path
