<#
.SYNOPSIS
Claims the files and folders the current slice will change, so every other session sees them, and prints what else
touches them; with -Release, takes the claim back.

.DESCRIPTION
A claim is a small text file named for the branch in the repository's git folder, which every worktree shares: the
branch, its worktree, the slice and its scope. The SessionStart hook lists the claims, and the edit hook refuses an
edit inside another claim's scope that the session's own claim doesn't cover. After writing the claim, the script
compares its scope with each other claim, except the one of the branch this one merges into, and with the files each
other worktree has changed against its own merge target. It prints a line for each overlap and exits 1 when there is
one; the claim is written either way. Run it from the slice's worktree, and again whenever the scope changes: a new
claim replaces the branch's old one. Runs from any current directory.

A claim ends on its own. Each run deletes every claim whose work is done: its worktree or branch is gone, or its
branch merged into its merge target. A claim goes idle, and no longer blocks or counts as an overlap, when nothing
was committed in its worktree and it was not claimed again for 3 days.

.PARAMETER Slice
What the slice does, in one line.

.PARAMETER Scope
The files and folders the slice will change, relative to the repository root, with / between folders. A folder covers
everything under it.

.PARAMETER Release
Removes the branch's claim instead.

.PARAMETER Branch
With -Release, the branch whose claim to remove, when it is not this worktree's: a coordinator's worker, for example.

.EXAMPLE
tools/Scripts/Claim.ps1 -Slice 'Fix the material cache leak' -Scope src/Core/Assets/MaterialCache.cs, src/Gems/Rendering

.EXAMPLE
tools/Scripts/Claim.ps1 -Release

.EXAMPLE
tools/Scripts/Claim.ps1 -Release -Branch feature/export-search
#>
[CmdletBinding()]
param(
    [string] $Slice,
    [string[]] $Scope = @(),
    [switch] $Release,
    [string] $Branch
)

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path (Join-Path (Join-Path $root '.claude') 'hooks') 'common.ps1')

# Whether two repository paths are the same, or one is a folder holding the other.
function Test-Overlap([string] $a, [string] $b) {
    $a = $a.ToLowerInvariant()
    $b = $b.ToLowerInvariant()

    return $a -eq $b -or $a.StartsWith("$b/") -or $b.StartsWith("$a/")
}

$checkout = Get-Checkout $root
$branch = [string](Invoke-Git $root @('branch', '--show-current'))
if (-not $checkout -or -not $checkout.IsWorktree -or -not $branch) {
    Write-Host "Claim from the slice's worktree, on its branch (AGENTS.md, Worktrees)."
    exit 1
}
$claims = Join-Path $checkout.Common 'agent-claims'
$own = Join-Path $claims (ConvertTo-BranchFileName $branch)

if ($Release) {
    if ($Branch) { $own = Join-Path $claims (ConvertTo-BranchFileName $Branch) }
    Remove-Item -LiteralPath $own -ErrorAction SilentlyContinue
    $null = @(Get-OpenClaims $checkout.Common)
    exit 0
}

# Write the claim. A caller outside PowerShell passes a list as one comma-separated argument.
$paths = @($Scope | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().Replace('\', '/').Trim('/') } |
        Where-Object { $_ })
if (-not $Slice -or $paths.Count -eq 0) {
    Write-Host 'Name the slice with -Slice and the files and folders it changes with -Scope.'
    exit 1
}
New-Item -ItemType Directory -Force -Path $claims | Out-Null
$lines = @("branch: $branch", "worktree: $($checkout.Top)", "slice: $Slice", "scope: $($paths -join ', ')")
[System.IO.File]::WriteAllText($own, ($lines -join "`n") + "`n")

# Other claims, except an idle one and the one of the branch this slice merges into, which holds it.
$overlaps = New-Object System.Collections.Generic.List[string]
foreach ($claim in @(Get-OpenClaims $checkout.Common)) {
    if ($claim.branch -in $branch, $checkout.Target -or $claim.state -eq 'idle') { continue }
    foreach ($path in $paths) {
        foreach ($theirs in $claim.scope) {
            if (Test-Overlap $path $theirs) { $overlaps.Add("${path}: claimed by $($claim.branch), $($claim.slice)") }
        }
    }
}

# Other worktrees' changes against their own merge targets, committed or not, which a missing claim can't hide. An
# idle worktree's do not count.
foreach ($line in @(Invoke-Git $root @('worktree', 'list', '--porcelain') | Where-Object { $_ })) {
    if ($line -notmatch '^worktree (.+)$') { continue }
    $top = $Matches[1]
    if ((ConvertTo-Key $top) -eq (ConvertTo-Key $checkout.Top) -or -not (Test-Path -LiteralPath $top)) { continue }
    $other = Get-Checkout $top
    $name = [string](Invoke-Git $top @('branch', '--show-current'))
    if (-not $other -or -not $other.Target -or $name -eq $checkout.Target) { continue }
    if (Test-WorktreeIdle $top (Join-Path $claims (ConvertTo-BranchFileName $name))) { continue }
    $base = [string](Invoke-Git $top @('merge-base', 'HEAD', $other.Target))
    if (-not $base) { continue }
    $changed = @(Invoke-Git $top @('diff', '--name-only', $base)) +
        @(Invoke-Git $top @('ls-files', '--others', '--exclude-standard'))
    foreach ($file in @($changed | Where-Object { $_ })) {
        foreach ($path in $paths) {
            if (Test-Overlap $path $file) { $overlaps.Add("${path}: changed in the worktree of $name, $top") }
        }
    }
}

if ($overlaps.Count -eq 0) { exit 0 }
Write-Host 'Claimed, but other work touches the same paths:'
$overlaps | Select-Object -Unique | ForEach-Object { Write-Host "  $_" }
Write-Host 'Ask the user whether to wait for it, narrow this slice, or go ahead (AGENTS.md, Worktrees).'
exit 1
