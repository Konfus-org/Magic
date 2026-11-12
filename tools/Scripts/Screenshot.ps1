<#
.SYNOPSIS
Runs a project with a window until its world has loaded, takes a screenshot through the console, and prints the image's
path; with -Domains, then opens each of them in turn and takes one of each.

.DESCRIPTION
Magic runs with --fail-on-error and these console lines piped in: name with -Name, wait loaded and wait drawn, which hold until the world has loaded and its
first frame is drawn, however long its shaders take to compile, a wait for what fades in to settle, any -Commands, a short wait for them, then
screenshot and a wait for it to be written; then, for each of -Domains, open, which holds until that domain has replaced
the world, the same waits, and screenshot; then exit. The log is shown as it comes; the script's only output is the
PNGs' paths, one a line in the order they were taken, then the window's captures with -Window, so a caller can keep
them: $png = tools/Scripts/Screenshot.ps1. Open each image and look at it: a screenshot that was taken is not one that
shows the change working. The run fails when Magic exits non-zero, when a world never finished loading within its
-Seconds, or when a screenshot is missing. Needs a display and a GPU, and a build (tools/Scripts/Validate.ps1 -Only
Build). Runs from any current directory.

.PARAMETER Configuration
The build configuration whose Magic runs: Debug, Optimize or Release.

.PARAMETER Settle
How many seconds pass after a world has loaded before its screenshot, for what fades in, streams in or gathers light
to settle.

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

.PARAMETER Name
The name the window shows in its title bar, so a person can tell whose run it is: the branch checked out where the
script is, which is the agent session's name. Empty sends no name.

.PARAMETER AllowErrors
Runs Magic without --fail-on-error, so a run that logs errors still takes its screenshots. The error lines of the log
are shown again before the paths.

.PARAMETER Window
Also captures the window's client area each time a screenshot is written, as the screenshot's path with -window before
.png. The capture shows what is drawn onto the window after the scene, such as a failure's banner and the debug UI,
and whatever covers the window on the screen. Needs Windows; elsewhere the script stops at once.

.EXAMPLE
tools/Scripts/Screenshot.ps1

.EXAMPLE
tools/Scripts/Screenshot.ps1 -Commands 'set Debug.RenderView Normal'

.EXAMPLE
tools/Scripts/Screenshot.ps1 -Project samples/Forest/Forest.magic

.EXAMPLE
tools/Scripts/Screenshot.ps1 -AllowErrors -Window
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Optimize', 'Release')]
    [string] $Configuration = 'Debug',
    [ValidateRange(0, [int]::MaxValue)]
    [int] $Settle = 20,
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Seconds = 600,
    [string] $Project = 'samples/HelloWorld/HelloWorld.magic',
    [string[]] $Commands = @(),
    [string[]] $Domains = @(),
    [string] $Name,
    [switch] $AllowErrors,
    [switch] $Window
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false

if ($Window -and [System.Environment]::OSVersion.Platform -ne 'Win32NT') {
    Write-Host 'The capture of the window needs Windows: run without -Window.'
    exit 1
}

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
# Another branch's profiling turn must not be disturbed: wait for it to end.
& (Join-Path $PSScriptRoot 'Profiling.ps1') -Wait
if (-not $PSBoundParameters.ContainsKey('Name')) { $Name = git -C $root branch --show-current }
$magic = Join-Path (Join-Path (Join-Path (Join-Path $root 'Build') 'bin') $Configuration) 'Magic'
if (Test-Path "$magic.exe") { $magic = "$magic.exe" }
if (-not (Test-Path $magic)) {
    Write-Host "There is no $magic to run: build first (tools/Scripts/Validate.ps1 -Only Build)."
    exit 1
}
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }

# The window shows whose run it is from the start. A screenshot is written by a worker, which takes seconds for a
# window-sized picture, so the run waits seconds after each, not frames, before it goes on or quits.
$lines = @(if ($Name) { "name `"$Name`"" }) + @('wait loaded', 'wait drawn', "wait ${Settle}s") + $Commands + @('wait 60', 'screenshot', 'wait 10s')
foreach ($domain in $Domains) {
    $lines += @("open $domain", "wait ${Settle}s", 'screenshot', 'wait 10s')
}
$lines += 'exit'
$arguments = @('open', "`"$projectPath`"")
if (-not $AllowErrors) { $arguments += '--fail-on-error' }

if ($Window) {
    Add-Type -AssemblyName System.Drawing
    # The client area of a window in screen pixels, as x, y, width and height, read by a process that sees the
    # screen's real pixels, not pixels scaled for the display's scale setting.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ClientArea
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Corner { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out Box box);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref Corner corner);

    public static int[] Of(IntPtr window)
    {
        SetProcessDPIAware();
        Box box;
        GetClientRect(window, out box);
        Corner corner = new Corner();
        ClientToScreen(window, ref corner);
        return new[] { corner.X, corner.Y, box.Right, box.Bottom };
    }
}
'@
}

# Copies what the screen shows of the window of $process to the PNG at $path.
function Save-Window([System.Diagnostics.Process] $process, [string] $path) {
    $process.Refresh()
    $area = [ClientArea]::Of($process.MainWindowHandle)
    $bitmap = New-Object System.Drawing.Bitmap $area[2], $area[3]
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($area[0], $area[1], 0, 0, $bitmap.Size)
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

# Magic runs with its console lines, its results and its log in files, so a run still going after its ceiling can be
# killed. The log is shown as it comes; the results, one screenshot's path per line, are read once it ends, and with
# -Window as they come, to capture the window while it still shows what the screenshot shows.
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("Screenshot-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$input_ = Join-Path $work 'lines.txt'
$results = Join-Path $work 'results.txt'
$log = Join-Path $work 'log.txt'
[System.IO.File]::WriteAllLines($input_, [string[]]$lines)
try {
    $process = Start-Process -FilePath $magic -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardInput $input_ -RedirectStandardOutput $results -RedirectStandardError $log
    # Holding the handle is what keeps the exit code readable once the process has ended.
    $null = $process.Handle
    $ceiling = [System.Diagnostics.Stopwatch]::StartNew()
    $shown = 0
    $captured = @()
    do {
        $hasEnded = $process.WaitForExit(1000)
        if (Test-Path $log) {
            $text = @(Get-Content $log -ErrorAction SilentlyContinue)
            for (; $shown -lt $text.Count; $shown++) { Write-Host $text[$shown] }
        }
        if ($Window -and -not $hasEnded) {
            $taken = @(Get-Content $results -ErrorAction SilentlyContinue)
            while ($captured.Count -lt $taken.Count) {
                $capture = $taken[$captured.Count] -replace '\.png$', '-window.png'
                Save-Window $process $capture
                $captured += $capture
            }
        }
        if (-not $hasEnded -and $ceiling.Elapsed.TotalSeconds -gt $Seconds * (1 + $Domains.Count)) {
            $process.Kill()
            Write-Host "FAILED: the run was still going after $($Seconds * (1 + $Domains.Count)) s."
            exit 1
        }
    } until ($hasEnded)
    $code = $process.ExitCode
    $text = @(Get-Content $log -ErrorAction SilentlyContinue)
    for (; $shown -lt $text.Count; $shown++) { Write-Host $text[$shown] }
    $found = @(Get-Content $results -ErrorAction SilentlyContinue)
    $errors = @($text | Where-Object { $_ -match '\[Error\]' })
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

if ($code -ne 0) {
    Write-Host "FAILED: Magic exited $code"
    exit $code
}
if ($found.Count -lt 1 + $Domains.Count) {
    Write-Host "FAILED: a world never finished loading within $Seconds s, so not every screenshot was taken."
    exit 1
}
if ($Window -and $captured.Count -lt $found.Count) {
    Write-Host 'FAILED: the run ended before the window was captured for each screenshot.'
    exit 1
}
foreach ($path in $found + $captured) {
    if (-not (Test-Path $path)) {
        Write-Host "FAILED: the results name $path but there is no such file."
        exit 1
    }
}
foreach ($line in $errors) { Write-Host $line }
foreach ($path in $found + $captured) { Write-Output $path }
