<#
.SYNOPSIS
What an agent runs on completing a task: tools/Format.ps1 on the change, tools/Validate.ps1 in full, then a stamp of
the exact state it validated, without which the project's hook refuses the agent's git commit and git merge.

.DESCRIPTION
Validation means the app ran with the change and its screenshot was looked at (AGENTS.md, Finish). This script first
formats the change, so what is validated, stamped and reviewed is the formatted code, then runs every step of
tools/Validate.ps1, the samples headless and with a window through every one of their domains, and when all
pass, stamps the checkout's state: the git tree of every file in it, tracked or new, as git add -A would record it, so
the stamp names content rather than a time. A checkout whose files changed while it ran is not stamped. The stamps are
kept in the repository's git folder, which every worktree shares, so a merge from the main checkout finds the stamp its
branch's worktree wrote. The script ends with the screenshots' paths, one of each domain: open each and look at it.

With -Hook it is that hook instead: the project's .claude/settings.json runs it before a Bash command that commits or
merges, with the hook's input as JSON on stdin. A git commit passes when the checkout's state has a stamp, and a
git merge when the merged branch's last commit does, so an edit after the run, or a rebase onto new work, needs a new
run. It exits 2 with the reason on stderr to refuse, which the agent is shown, and 0 to let the command run; any other
command, or one it cannot place in a checkout, passes. Runs under pwsh and Windows PowerShell, from any directory.

.PARAMETER Hook
Check the git commit or git merge the hook's input on stdin names, rather than validate.

.EXAMPLE
tools/AgentTaskComplete.ps1
#>
[CmdletBinding()]
param(
    [switch] $Hook
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false

# The git tree of every file in a checkout, tracked or new, as git add -A would record it, built in an index of its own
# so the checkout's own index, and what is staged in it, is left as it is. It starts from a copy of that index so only
# files changed since are read again.
function Get-CheckoutTree([string] $checkout) {
    $index = Join-Path ([System.IO.Path]::GetTempPath()) ("AgentTaskComplete-" + [guid]::NewGuid() + '.index')
    $own = & git -C $checkout rev-parse --path-format=absolute --git-path index
    if (Test-Path $own) { Copy-Item $own $index }
    $env:GIT_INDEX_FILE = $index
    try {
        # Line-ending warnings go to stderr, which Windows PowerShell would otherwise throw on.
        $ErrorActionPreference = 'Continue'
        & git -C $checkout add --all 2>$null
        if ($LASTEXITCODE -ne 0) { throw "git add could not read $checkout." }
        return [string](& git -C $checkout write-tree)
    } finally {
        $ErrorActionPreference = 'Stop'
        Remove-Item env:GIT_INDEX_FILE
        Remove-Item $index -ErrorAction SilentlyContinue
    }
}

# Where the stamp of a tree is kept: the repository's git folder, which every worktree of it shares.
function Get-StampPath([string] $checkout, [string] $tree) {
    $common = & git -C $checkout rev-parse --path-format=absolute --git-common-dir
    return Join-Path (Join-Path $common 'agent-task-complete') $tree
}

# A Git Bash path (/c/Users/...) as Windows takes it.
function ConvertTo-Native([string] $path) {
    if ($path -match '^/([a-zA-Z])/(.*)$') { return "$($Matches[1]):/$($Matches[2])" }
    return $path
}

if (-not $Hook) {
    $root = Split-Path -Parent $PSScriptRoot
    & (Join-Path $PSScriptRoot 'Format.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $before = Get-CheckoutTree $root
    & (Join-Path $PSScriptRoot 'Validate.ps1') | Tee-Object -Variable lines
    $code = $LASTEXITCODE
    if ($code -ne 0) { exit $code }

    $after = Get-CheckoutTree $root
    if ($after -ne $before) {
        Write-Output 'FAILED: files in the checkout changed while it was being validated, so what passed is not what is there: run it again.'
        exit 1
    }

    $screenshots = @($lines | Where-Object { $_ -like 'Screenshot: *' } | ForEach-Object { $_.Substring(12) })
    $stamp = Get-StampPath $root $after
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stamp) | Out-Null
    Set-Content -Path $stamp -Value $screenshots -Encoding utf8
    Write-Output ""
    Write-Output "Stamped this state ($after): git commit and git merge of it are allowed."
    Write-Output 'Open each screenshot and look at what it shows before reporting the task complete:'
    foreach ($path in $screenshots) { Write-Output "  $path" }
    exit 0
}

$hookInput = [Console]::In.ReadToEnd() | ConvertFrom-Json
$command = [string]$hookInput.tool_input.command
$here = [string]$hookInput.cwd

# commit or merge as git's own command, not git commit-tree or git merge-base.
if ($command -notmatch '\bgit\b(\s+-C\s+\S+)?\s+(commit|merge)(?![-\w])') { exit 0 }
$isMerge = $Matches[2] -eq 'merge'

# The folder the git command runs in: the one a git -C or a cd before it names, else where the command runs.
$folder = $here
if ($command -match 'git\s+-C\s+(?:"([^"]+)"|''([^'']+)''|(\S+))') {
    $folder = @($Matches[1], $Matches[2], $Matches[3]) | Where-Object { $_ } | Select-Object -First 1
} elseif ($command -match '\bcd\s+(?:"([^"]+)"|''([^'']+)''|(\S+))') {
    $folder = @($Matches[1], $Matches[2], $Matches[3]) | Where-Object { $_ } | Select-Object -First 1
}
$folder = ConvertTo-Native $folder
if (-not [System.IO.Path]::IsPathRooted($folder)) { $folder = Join-Path (ConvertTo-Native $here) $folder }
if (-not (Test-Path $folder)) { exit 0 }

$top = & git -C $folder rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or -not $top) { exit 0 }

if ($isMerge) {
    # The branch merged: the first word after merge that is not an option. merge --abort or --continue names none.
    $words = @(($command -split '\bmerge\b', 2)[1] -split '[\s;&|]+' | Where-Object { $_ -and -not $_.StartsWith('-') })
    if ($words.Count -eq 0) { exit 0 }
    $branch = $words[0].Trim('"', "'")
    $tree = & git -C $top rev-parse --verify --quiet "$branch^{tree}"
    if ($LASTEXITCODE -ne 0 -or -not $tree) { exit 0 }
    $what = "$branch as its last commit has it"
    $where = "the worktree of $branch"
} else {
    $tree = Get-CheckoutTree $top
    $what = "$top as it is now"
    $where = $top
}

if (Test-Path (Get-StampPath $top $tree)) { exit 0 }

[Console]::Error.WriteLine("$what was never validated: run tools/AgentTaskComplete.ps1 in $where, open the screenshot it prints and look at it, then try again (AGENTS.md, Finish). An edit after the run, or a rebase onto new work, needs a new run.")
exit 2
