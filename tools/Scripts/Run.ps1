<#
.SYNOPSIS
Runs Magic on a project for an agent: names its window for the session, sends it console lines, and ends the run at
its time limit. Agents start Magic only through this script and the scripts that run Magic themselves.

.DESCRIPTION
Magic opens the project with --name, the worktree's branch (or its folder and short commit on a detached HEAD), when
it has a window, so the window shows whose run it is. Then it reads the console lines of -Commands. End -Commands with
exit to end the run. Without exit, a run with a window goes on until the user closes it. A run still going after -Seconds is killed. With -Headless, the run has no
window and exits 2 at the first error logged. The log is shown as it comes, and the script's output is what Magic
prints, such as a screenshot's path, also when the run was killed. The script exits with Magic's exit code, or 1 when it
killed the run. It doesn't build. Runs from any current directory.

.PARAMETER Configuration
The build configuration whose Magic runs: Debug, Optimize or Release. Use Release only when the user asks for it.

.PARAMETER Project
The project to run: a .magic file or the folder holding one, relative to the repository root.

.PARAMETER Commands
Console lines sent once the run starts. `help` in a run lists them.

.PARAMETER Headless
Runs without a window, and ends the run at the first error logged.

.PARAMETER Seconds
The longest the run may last, in seconds: a ceiling, not a wait. A run still going then is killed.

.PARAMETER Configure
Settings that the run starts with, as `Category.Member=value`, such as `Debug.IsSyncValidationOn=true`. Magic gets them
with `--configure`.

.PARAMETER Display
The display that the window opens on, by its place in SDL's list of displays, where the main display is first. The
default, 1, is the display after the main one, so that the run does not cover the user's work. With one display, the
window opens on the main one.

.PARAMETER Name
The window's name: the worktree's branch, the session's name, when left out; on a detached HEAD, the worktree's folder
name and the short commit. A window with no name stops the run with an error.

.EXAMPLE
tools/Scripts/Run.ps1 -Project samples/MountainCabin/MountainCabin.magic

.EXAMPLE
tools/Scripts/Run.ps1 -Headless -Commands 'wait loaded', 'query player.transform', 'exit'
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Optimize', 'Release')]
    [string] $Configuration = 'Debug',
    [string] $Project = 'samples/MountainCabin/MountainCabin.magic',
    [string[]] $Commands = @(),
    [switch] $Headless,
    [string[]] $Configure = @(),
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Seconds = 600,
    [ValidateRange(0, [int]::MaxValue)]
    [int] $Display = 1,
    [string] $Name
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false

. (Join-Path $PSScriptRoot 'RunLib.ps1')

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
# Another branch's profiling turn must not be disturbed: wait for it to end.
& (Join-Path $PSScriptRoot 'Profiling.ps1') -Wait
if (-not $PSBoundParameters.ContainsKey('Name')) { $Name = Get-RunName $root }
if (-not $Headless) { Assert-RunName $Name }
$magic = Get-MagicPath $Configuration
if (-not (Test-Path $magic)) {
    Write-Host "There is no $magic to run: build first (tools/Scripts/Validate.ps1 -Only Build)."
    exit 1
}
$projectPath = Resolve-ProjectFile $Project $root

$lines = $Commands
$arguments = @('open', "`"$projectPath`"")
if ($Headless) {
    $arguments += @('--headless', 'true', '--fail-on-error')
} else {
    # The window shows whose run it is from the moment it opens. A headless run has no window to name.
    $arguments += @('--name', "`"$Name`"")
    $Display = Limit-Display $Display
    # The window opens on -Display, behind the window that has the focus, so that it does not interrupt the user's work.
    # SDL reads the hint from the environment, and Magic inherits it.
    $Configure += "Window.Display=$Display"
    $env:SDL_WINDOW_ACTIVATE_WHEN_SHOWN = '0'
}
if ($Configure.Count -gt 0) { $arguments += @('--configure') + ($Configure | ForEach-Object { "`"$_`"" }) }

# Magic runs with its console lines, its output and its log in files, so a run still going after -Seconds can be
# killed. The log is shown as it comes; the output is written once the run ends.
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("Run-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$input_ = Join-Path $work 'lines.txt'
$output = Join-Path $work 'output.txt'
$log = Join-Path $work 'log.txt'
[System.IO.File]::WriteAllLines($input_, [string[]]$lines)
# Only a few runs of Magic go at the same time, from all sessions: wait for a free slot, and hold it for the run.
$slot = & (Join-Path $PSScriptRoot 'Profiling.ps1') -Slot
try {
    $process = Start-Process -FilePath $magic -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardInput $input_ -RedirectStandardOutput $output -RedirectStandardError $log
    # Holding the handle is what keeps the exit code readable once the process has ended.
    $null = $process.Handle
    $ceiling = [System.Diagnostics.Stopwatch]::StartNew()
    $shown = 0
    $killed = $false
    do {
        $hasEnded = $process.WaitForExit(1000)
        if (Test-Path $log) {
            $text = @(Get-Content $log -ErrorAction SilentlyContinue)
            for (; $shown -lt $text.Count; $shown++) { Write-Host $text[$shown] }
        }
        if (-not $hasEnded -and $ceiling.Elapsed.TotalSeconds -gt $Seconds) {
            $process.Kill()
            $null = $process.WaitForExit(10000)
            $killed = $true
            break
        }
    } until ($hasEnded)
    $code = if ($killed) { 1 } else { $process.ExitCode }
    $text = @(Get-Content $log -ErrorAction SilentlyContinue)
    for (; $shown -lt $text.Count; $shown++) { Write-Host $text[$shown] }
    Get-Content $output -ErrorAction SilentlyContinue | ForEach-Object { Write-Output $_ }
} finally {
    $slot.Dispose()
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

if ($killed) { Write-Host "The run was still going after $Seconds s, so it was ended." }
elseif ($code -ne 0) { Write-Host "FAILED: Magic exited $code" }
exit $code
