<#
.SYNOPSIS
Runs a project with a window until its world has loaded, takes a screenshot through the console, and prints the image's
path; with -Domains, then opens each of them in turn and takes one of each.

.DESCRIPTION
Magic runs with --fail-on-error and these console lines piped in: wait loaded, which holds until the world has loaded
however long its shaders take to compile, a wait for what fades in to settle, any -Commands, a short wait for them, then
screenshot; then, for each of -Domains, open, which holds until that domain has replaced the world, the same wait, and
screenshot; then quit. The log is shown as it comes; the script's only output is the PNGs' paths, one a line in the
order they were taken, so a caller can keep them: $png = tools/Scripts/Screenshot.ps1. Open each image and look at it: a
screenshot that was taken is not one that shows the change working. The run fails when Magic exits non-zero, when a
world never finished loading within its -Seconds, or when a screenshot is missing. Needs a display and a GPU, and a
build (tools/Scripts/Validate.ps1 -Only Build). Runs from any current directory.

.PARAMETER Configuration
The build configuration whose Magic runs: Debug, Optimize or Release.

.PARAMETER Settle
How many frames pass after a world has loaded before its screenshot, for what fades in, streams in or gathers light
over frames to settle.

.PARAMETER Seconds
The longest the run may last for each world it loads, in seconds: a ceiling, not a wait. A run that has not loaded
its worlds by then fails.

.PARAMETER Project
The project to run: a .magic file or the folder holding one, relative to the repository root.

.PARAMETER Commands
Console lines sent after the world has loaded and settled, before the first screenshot, such as one changing a
setting. `help` in a run lists them.

.PARAMETER Domains
Domains to open after the first screenshot, each by its asset path, a screenshot taken of each once it has settled.

.EXAMPLE
tools/Scripts/Screenshot.ps1

.EXAMPLE
tools/Scripts/Screenshot.ps1 -Commands 'set RealtimeGIRendering.renderScale=1'

.EXAMPLE
tools/Scripts/Screenshot.ps1 -Domains 'Domains/Valley/Valley.domain'
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Optimize', 'Release')]
    [string] $Configuration = 'Debug',
    [ValidateRange(0, [int]::MaxValue)]
    [int] $Settle = 600,
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Seconds = 600,
    [string] $Project = 'samples',
    [string[]] $Commands = @(),
    [string[]] $Domains = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$magic = Join-Path (Join-Path (Join-Path (Join-Path $root 'Build') 'bin') $Configuration) 'Magic'
if (Test-Path "$magic.exe") { $magic = "$magic.exe" }
if (-not (Test-Path $magic)) {
    Write-Host "There is no $magic to run: build first (tools/Scripts/Validate.ps1 -Only Build)."
    exit 1
}
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }

# The screenshot is written by a worker, so the run goes on a few frames before it quits.
$lines = @('wait loaded', "wait $Settle") + $Commands + @('wait 60', 'screenshot')
foreach ($domain in $Domains) {
    $lines += @("open $domain", "wait $Settle", 'screenshot')
}
$lines += @('wait 60', 'quit')
$log = $lines | & $magic open $projectPath --seconds ($Seconds * (1 + $Domains.Count)) --fail-on-error | ForEach-Object { Write-Host $_; $_ }
$code = $LASTEXITCODE

# A line reads "12:34:56.789 Info     f1234 t1 Screenshot: <path> (File.cs:60 Member)".
$found = @($log | Select-String -Pattern 'Screenshot: (.+\.png) \(' | ForEach-Object { $_.Matches[0].Groups[1].Value })
if ($code -ne 0) {
    Write-Host "FAILED: Magic exited $code"
    exit $code
}
if ($found.Count -lt 1 + $Domains.Count) {
    Write-Host "FAILED: a world never finished loading within $Seconds s, so not every screenshot was taken."
    exit 1
}
foreach ($path in $found) {
    if (-not (Test-Path $path)) {
        Write-Host "FAILED: the log names $path but there is no such file."
        exit 1
    }
}
foreach ($path in $found) { Write-Output $path }
