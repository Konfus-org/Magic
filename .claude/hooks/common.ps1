# Shared by the hooks in this folder. Runs under Windows PowerShell 5.1 and pwsh.
# Every hook fails open: an error in a hook lets the action through rather than blocking work.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Read-HookInput { return ([Console]::In.ReadToEnd() | ConvertFrom-Json) }

function Get-Field($object, [string] $name) {
    if ($null -ne $object -and $object.PSObject.Properties[$name]) { return $object.$name }
    return $null
}

function Invoke-Git([string] $folder, [string[]] $arguments) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -C $folder @arguments 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return $out
    } finally {
        $ErrorActionPreference = $saved
    }
}

function ConvertTo-Key([string] $path) { return $path.Replace('\', '/').TrimEnd('/').ToLowerInvariant() }

function Write-HookJson($value) { [Console]::Out.Write(($value | ConvertTo-Json -Compress -Depth 6)) }

# The checkout a path is in: its top folder, whether it is a linked worktree, the repository's shared git folder,
# and the merge target: the branch's upstream when that is a local branch, as a swarm worker's root is, otherwise the
# branch checked out in the main checkout. Two git calls.
function Get-Checkout([string] $path) {
    if (-not $path) { return $null }
    $folder = $path
    while ($folder -and -not (Test-Path -LiteralPath $folder -PathType Container)) { $folder = Split-Path -Parent $folder }
    if (-not $folder) { return $null }
    $lines = @(Invoke-Git $folder @('rev-parse', '--path-format=absolute', '--show-toplevel', '--git-dir', '--git-common-dir'))
    if ($lines.Count -lt 3) { return $null }
    $common = [string]$lines[2]
    $target = $null
    $upstream = [string](Invoke-Git $folder @('rev-parse', '--symbolic-full-name', '@{upstream}'))
    $head = Join-Path $common 'HEAD'
    if ($upstream -match '^refs/heads/(.+)$') {
        $target = $Matches[1]
    } elseif (Test-Path -LiteralPath $head) {
        $ref = [string](Get-Content -LiteralPath $head -TotalCount 1)
        if ($ref -match '^ref: refs/heads/(.+)$') { $target = $Matches[1].Trim() }
    }
    return [pscustomobject]@{
        Top        = [string]$lines[0]
        Common     = $common
        Target     = $target
        IsWorktree = (ConvertTo-Key ([string]$lines[1])) -ne (ConvertTo-Key $common)
    }
}

# Whether a checkout belongs to the repository that this file is in.
function Test-ThisRepository($checkout) {
    $project = [string](Invoke-Git $PSScriptRoot @('rev-parse', '--path-format=absolute', '--git-common-dir'))
    return (ConvertTo-Key $project) -eq (ConvertTo-Key $checkout.Common)
}

# A branch's name as one file name, for the stamps kept per branch.
function ConvertTo-BranchFileName([string] $branch) { return $branch -replace '[\\/:]', '__' }

# The last QA verdict of a branch. The stamp's first line is the commit that QA checked, the rest the QA report.
function Get-ReviewStamp($checkout, [string] $branch) {
    return Join-Path (Join-Path $checkout.Common 'agent-review') (ConvertTo-BranchFileName $branch)
}

# The verdict of a QA report: the word on its last line that starts with VERDICT:, or nothing.
function Get-Verdict([string] $report) {
    $found = [regex]::Matches($report, '(?m)^\s*VERDICT:\s*(PASS|FINDINGS)\b')
    if ($found.Count -eq 0) { return $null }
    return $found[$found.Count - 1].Groups[1].Value
}

# Whether the work of a branch, in its worktree $top, is in its merge target: a merge of the branch into the target
# changes nothing, which is also true after a squash merge, the target has moved on since, and the worktree has no
# change that is not committed. A branch with no commit of its own, as its reflog shows, is a slice that just started,
# so it does not count.
function Test-BranchMerged([string] $top, [string] $branch, [string] $target) {
    if (-not $target -or $branch -eq $target) { return $false }
    $merged = [string](Invoke-Git $top @('merge-tree', '--write-tree', $target, $branch))
    return $merged -and $merged -eq [string](Invoke-Git $top @('rev-parse', "$target^{tree}")) -and
        -not (Invoke-Git $top @('status', '--porcelain')) -and
        [string](Invoke-Git $top @('rev-parse', $branch)) -ne [string](Invoke-Git $top @('rev-parse', $target)) -and
        @(Invoke-Git $top @('reflog', 'show', '--format=%gs', "refs/heads/$branch")).Count -gt 1
}
