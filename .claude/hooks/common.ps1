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
# and the merge target, which is the branch checked out in the main checkout.
function Get-Checkout([string] $path) {
    if (-not $path) { return $null }
    $folder = $path
    while ($folder -and -not (Test-Path -LiteralPath $folder -PathType Container)) { $folder = Split-Path -Parent $folder }
    if (-not $folder) { return $null }
    $top = Invoke-Git $folder @('rev-parse', '--show-toplevel')
    if (-not $top) { return $null }
    $gitDir = Invoke-Git $folder @('rev-parse', '--path-format=absolute', '--git-dir')
    $common = Invoke-Git $folder @('rev-parse', '--path-format=absolute', '--git-common-dir')
    $main = $null
    $target = $null
    foreach ($line in @(Invoke-Git $folder @('worktree', 'list', '--porcelain'))) {
        if (-not $main) {
            if ($line -like 'worktree *') { $main = $line.Substring(9) }
            continue
        }
        if ($line -like 'branch refs/heads/*') { $target = $line.Substring(18); break }
        if ($line -eq '') { break }
    }
    return [pscustomobject]@{
        Top        = [string]$top
        Common     = [string]$common
        Main       = [string]$main
        Target     = $target
        IsWorktree = (ConvertTo-Key ([string]$gitDir)) -ne (ConvertTo-Key ([string]$common))
    }
}

# Whether a checkout belongs to the repository this session was started in.
function Test-ThisRepository($checkout) {
    if (-not $checkout -or -not $env:CLAUDE_PROJECT_DIR) { return $false }
    $project = Invoke-Git $env:CLAUDE_PROJECT_DIR @('rev-parse', '--path-format=absolute', '--git-common-dir')
    return [bool]$project -and (ConvertTo-Key ([string]$project)) -eq (ConvertTo-Key $checkout.Common)
}

# The worktree this session last edited, which the PostToolUse hook records, for hooks whose cwd is elsewhere.
function Get-SessionRecord($checkoutCommon, [string] $sessionId) {
    if (-not $sessionId) { return $null }
    return Join-Path (Join-Path $checkoutCommon 'agent-sessions') $sessionId
}

# The checkout a hook should judge: its cwd when that is a linked worktree, else the worktree the session edited last.
function Resolve-SliceCheckout($hook) {
    $cwd = [string](Get-Field $hook 'cwd')
    if (-not $cwd) { $cwd = $env:CLAUDE_PROJECT_DIR }
    $checkout = Get-Checkout $cwd
    if ($checkout -and $checkout.IsWorktree) { return $checkout }
    $project = Get-Checkout $env:CLAUDE_PROJECT_DIR
    if (-not $project) { return $null }
    $record = Get-SessionRecord $project.Common ([string](Get-Field $hook 'session_id'))
    if (-not $record -or -not (Test-Path -LiteralPath $record)) { return $null }
    $recorded = Get-Checkout ([string](Get-Content -LiteralPath $record -TotalCount 1))
    if ($recorded -and $recorded.IsWorktree) { return $recorded }
    return $null
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

function Get-ReviewStamp($checkout, [string] $tree) {
    return Join-Path (Join-Path $checkout.Common 'agent-review') $tree
}
