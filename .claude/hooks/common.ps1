# Shared by the hooks in this folder and tools/Scripts/Claim.ps1. Runs under Windows PowerShell 5.1 and pwsh.
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
# and the merge target, which is the branch checked out in the main checkout. One git call.
function Get-Checkout([string] $path) {
    if (-not $path) { return $null }
    $folder = $path
    while ($folder -and -not (Test-Path -LiteralPath $folder -PathType Container)) { $folder = Split-Path -Parent $folder }
    if (-not $folder) { return $null }
    $lines = @(Invoke-Git $folder @('rev-parse', '--path-format=absolute', '--show-toplevel', '--git-dir', '--git-common-dir'))
    if ($lines.Count -lt 3) { return $null }
    $common = [string]$lines[2]
    $target = $null
    $head = Join-Path $common 'HEAD'
    if (Test-Path -LiteralPath $head) {
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

# Whether a checkout belongs to the repository this session was started in.
function Test-ThisRepository($checkout) {
    if (-not $checkout -or -not $env:CLAUDE_PROJECT_DIR) { return $false }
    $project = Invoke-Git $env:CLAUDE_PROJECT_DIR @('rev-parse', '--path-format=absolute', '--git-common-dir')
    return [bool]$project -and (ConvertTo-Key ([string]$project)) -eq (ConvertTo-Key $checkout.Common)
}

# The checkout's state as tools/Scripts/AgentTaskComplete.ps1 stamps it: the git tree of every file, tracked or new,
# built in an index of its own. Also the lines changed against the merge base with the merge target.
function Get-SliceState($checkout) {
    $top = $checkout.Top
    $index = Join-Path ([System.IO.Path]::GetTempPath()) ('MagicHook-' + [guid]::NewGuid() + '.index')
    $own = Invoke-Git $top @('rev-parse', '--path-format=absolute', '--git-path', 'index')
    if ($own -and (Test-Path -LiteralPath ([string]$own))) { Copy-Item -LiteralPath ([string]$own) $index }
    $env:GIT_INDEX_FILE = $index
    try {
        $null = Invoke-Git $top @('add', '--all')
        $tree = [string](Invoke-Git $top @('write-tree'))
        $base = $null
        $baseTree = $null
        $lines = 0
        if ($checkout.Target) { $base = Invoke-Git $top @('merge-base', 'HEAD', $checkout.Target) }
        if ($base) {
            $baseTree = [string](Invoke-Git $top @('rev-parse', "$base^{tree}"))
            foreach ($row in @(Invoke-Git $top @('diff', '--cached', '--numstat', [string]$base))) {
                $parts = ([string]$row) -split "`t"
                if ($parts.Count -ge 2 -and $parts[0] -match '^\d+$' -and $parts[1] -match '^\d+$') {
                    $lines += [int]$parts[0] + [int]$parts[1]
                }
            }
        }
        return [pscustomobject]@{ Tree = $tree; BaseTree = $baseTree; Lines = $lines }
    } finally {
        Remove-Item env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $index -ErrorAction SilentlyContinue
    }
}

function Get-ValidationStamp($checkout, [string] $tree) {
    return Join-Path (Join-Path $checkout.Common 'agent-task-complete') $tree
}

# A branch's name as one file name, for the stamps and claims kept per branch.
function ConvertTo-BranchFileName([string] $branch) { return $branch -replace '[\\/:]', '__' }

# A slice is reviewed once per branch. The stamp's first line is the slice's changed lines when it was reviewed.
function Get-ReviewStamp($checkout, [string] $branch) {
    return Join-Path (Join-Path $checkout.Common 'agent-review') (ConvertTo-BranchFileName $branch)
}

# A claim tools/Scripts/Claim.ps1 wrote: its branch, worktree, slice and scope, one key a line.
function Read-Claim([string] $path) {
    $claim = @{ branch = ''; worktree = ''; slice = ''; scope = '' }
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        if ($line -match '^(\w+):\s*(.*)$') { $claim[$Matches[1]] = $Matches[2] }
    }
    $claim.scope = @(([string]$claim.scope) -split ',\s*' | Where-Object { $_ })

    return [pscustomobject]$claim
}

# Whether a claim's worktree still exists. One whose worktree is gone is stale, and the tidy-worktrees skill removes it.
function Test-ClaimLive($claim) { return [bool]$claim.worktree -and (Test-Path -LiteralPath $claim.worktree) }
