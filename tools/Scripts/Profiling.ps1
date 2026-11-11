<#
.SYNOPSIS
Takes, waits for or releases a profiling turn: the time in which one worktree alone builds and runs Magic, so that its
profile or measurement of CPU, RAM or GPU use is not disturbed.

.DESCRIPTION
A turn is a small file in the repository's git folder, which every worktree shares: the worktree that holds it, its
branch and the time that it ends. -Minutes takes the turn for the worktree that the script is in. It first waits, up to
ten minutes, until no run of Magic, build or test run from another worktree is going, and the minutes start after
that. It fails when another worktree holds a turn that has not ended, or when the other runs don't end in time.
-Release ends the worktree's turn: run the copy of the script in the worktree that holds it. -Wait waits while another
worktree holds a turn, and returns at once otherwise: the scripts that build or run Magic call it before each step. A
turn ends by itself at its time, so a holder that stops can't block the others. Runs from any current directory.

.PARAMETER Minutes
Takes the turn for this many minutes, at most 20.

.PARAMETER Release
Ends this worktree's turn.

.PARAMETER Wait
Waits while another worktree holds a turn.

.EXAMPLE
tools/Scripts/Profiling.ps1 -Minutes 15

.EXAMPLE
tools/Scripts/Profiling.ps1 -Release
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 20)]
    [int] $Minutes,
    [switch] $Release,
    [switch] $Wait
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$common = [string](git -C $root rev-parse --path-format=absolute --git-common-dir)
$branch = [string](git -C $root branch --show-current)
$turn = Join-Path $common 'agent-profiling'

# A path as the turn file and the checks keep it: full, with / between folders, lower case, no / at the end.
function ConvertTo-Key([string] $path) {
    return [System.IO.Path]::GetFullPath($path).Replace('\', '/').TrimEnd('/').ToLowerInvariant()
}
$own = ConvertTo-Key $root

# The turn that has not ended yet: its holder's worktree, its branch and the time it ends, in Unix seconds. Nothing
# when there is none. A file that a release deletes while it is read counts as none.
function Get-Turn {
    try {
        $lines = [System.IO.File]::ReadAllLines($turn)
    } catch [System.IO.IOException] {
        return $null
    }
    $holder = ''
    $name = ''
    $until = 0
    foreach ($line in $lines) {
        if ($line -match '^holder:\s*(.+)$') { $holder = $Matches[1].Trim() }
        if ($line -match '^branch:\s*(.*)$') { $name = $Matches[1].Trim() }
        if ($line -match '^until:\s*(\d+)') { $until = [long]$Matches[1] }
    }
    if (-not $holder -or $until -le [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) { return $null }

    return [pscustomobject]@{ Holder = $holder; Branch = $name; Until = $until }
}

function Format-Time([long] $seconds) {
    return [DateTimeOffset]::FromUnixTimeSeconds($seconds).LocalDateTime.ToString('HH:mm')
}

# The runs of Magic, builds and test runs whose program or command line is in another worktree of the repository.
function Get-OtherRuns {
    $others = @(git -C $root worktree list --porcelain | Where-Object { $_ -like 'worktree *' } |
            ForEach-Object { (ConvertTo-Key $_.Substring(9)) + '/' } | Where-Object { $_ -ne "$own/" })
    if ($others.Count -eq 0) { return @() }
    $names = @('Magic', 'dotnet', 'MSBuild', 'testhost')
    if ([System.Environment]::OSVersion.Platform -eq 'Win32NT') {
        $processes = @(Get-CimInstance Win32_Process |
                Where-Object { $names -contains [System.IO.Path]::GetFileNameWithoutExtension($_.Name) } |
                ForEach-Object {
                    [pscustomobject]@{ Id = $_.ProcessId; Text = "$($_.ExecutablePath) $($_.CommandLine)" }
                })
    } else {
        $processes = @(Get-Process -Name $names -ErrorAction SilentlyContinue |
                ForEach-Object { [pscustomobject]@{ Id = $_.Id; Text = "$($_.Path) $($_.CommandLine)" } })
    }
    $runs = @()
    foreach ($process in $processes) {
        $text = $process.Text.Replace('\', '/').ToLowerInvariant()
        foreach ($other in $others) {
            if ($text.Contains($other)) {
                $runs += "$($process.Text.Trim()) (process $($process.Id))"
                break
            }
        }
    }

    return $runs
}

if ($Wait) {
    $shown = $false
    $current = Get-Turn
    while ($null -ne $current -and $current.Holder -ne $own) {
        if (-not $shown) {
            $ends = Format-Time $current.Until
            Write-Host "Waiting for the profiling turn of $($current.Branch), which ends at $ends."
            $shown = $true
        }
        Start-Sleep -Seconds 5
        $current = Get-Turn
    }
    exit 0
}

if ($Release) {
    try {
        if ([System.IO.File]::ReadAllText($turn) -match "(?m)^holder:\s*$([regex]::Escape($own))\s*$") {
            Remove-Item -LiteralPath $turn
        }
    } catch [System.IO.IOException] {
        # There is no turn to release.
    }
    exit 0
}

if (-not $PSBoundParameters.ContainsKey('Minutes')) {
    Write-Host 'Take a turn with -Minutes, or use -Release or -Wait.'
    exit 1
}

# Take the turn: refuse a turn that another worktree holds, clear an ended one, and create the file only if no other
# take created it first.
$current = Get-Turn
if ($null -ne $current -and $current.Holder -ne $own) {
    Write-Host "$($current.Branch) holds the profiling turn until $(Format-Time $current.Until)."
    exit 1
}
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(10).ToUnixTimeSeconds()
$text = "holder: $own`nbranch: $branch`nuntil: $($deadline + $Minutes * 60)`n"
if ($null -eq $current) {
    try {
        if (Test-Path -LiteralPath $turn) { Remove-Item -LiteralPath $turn }
        $file = [System.IO.File]::Open($turn, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        $file.Write($bytes, 0, $bytes.Length)
        $file.Dispose()
    } catch [System.IO.IOException] {
        $current = Get-Turn
        $who = if ($current) { $current.Branch } else { 'Another worktree' }
        Write-Host "$who took the profiling turn first."
        exit 1
    }
} else {
    [System.IO.File]::WriteAllText($turn, $text)
}

# Runs that started before the turn still disturb it, so wait up to ten minutes for them to end. The turn's own
# minutes start after that.
$shown = $false
$runs = @(Get-OtherRuns)
while ($runs.Count -gt 0 -and [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -lt $deadline) {
    if (-not $shown) {
        Write-Host "Waiting for $($runs.Count) run(s) from other worktrees to end."
        $shown = $true
    }
    Start-Sleep -Seconds 5
    $runs = @(Get-OtherRuns)
}
if ($runs.Count -gt 0) {
    Remove-Item -LiteralPath $turn
    Write-Host 'These runs from other worktrees still run after ten minutes, so the turn was not taken:'
    $runs | ForEach-Object { Write-Host "  $_" }
    exit 1
}

$until = [DateTimeOffset]::UtcNow.AddMinutes($Minutes).ToUnixTimeSeconds()
[System.IO.File]::WriteAllText($turn, "holder: $own`nbranch: $branch`nuntil: $until`n")
Write-Host "The profiling turn is $branch's until $(Format-Time $until). Release it with -Release when done."
exit 0
