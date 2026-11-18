<#
.SYNOPSIS
What an agent runs on completing a task: tools/Scripts/Format.ps1 on the change, the steps of tools/Scripts/Validate.ps1 that what the
change can affect needs, then a stamp of the exact state it validated, which a git merge in the main checkout needs.

.DESCRIPTION
-Change says what the change can affect, and so what must run to show it works (AGENTS.md, Finish). Docs is a change
to nothing Magic builds or loads, such as Markdown, comments, docstrings and the tool files in tools/Scripts, .claude
and .githooks: nothing runs, no format among it, and the state is stamped at once. A change with another file is
refused as Docs, unless every line it changes in a .cs or .slang file is a // comment. Code is any change to what runs, a refactor among them, as a rename can break a lookup by
name that only a run finds: the tests run too, and the sample project headless. Visual is
any change that can alter what is drawn: the samples run with a window through every domain instead, which load and
render as the headless run does, ending in a screenshot of each to open and look at.

The script first formats the change, so what is validated, stamped and reviewed is the formatted code, then runs those
steps, and when all pass, stamps the checkout's state: the git tree of every file in it, tracked or new, as git add -A
would record it, so the stamp names content rather than a time. When every file that the change touches, Markdown
aside, is in the folder of a sample, only the samples that it touches run. A checkout whose files changed while it ran is not
stamped. The stamps are kept in the repository's git folder, which every worktree shares, so a merge from the main
checkout finds the stamp its branch's worktree wrote. A stamp keeps its level and its screenshots, and a state already
stamped at the level asked for or a higher one is not validated again, so a run after a pull that brought in
nothing ends at once. A pull that git merged with no conflict, of a stamped state into a state stamped at the level
asked for or a higher one, runs only the build. The script ends with the screenshots' paths, one of each domain: open each and look at it. It
also points git at the repository's hooks (.githooks) when the clone has no hooks path, so every commit's subject gets
its type.

With -Hook it is a guard instead, for a tool hook that runs it before a command that merges, with the hook's input
as JSON on stdin. A git merge in the main checkout passes when the merged branch's last commit has a stamp, so an edit
after the run, or a pull of new work, needs a new run. Commits, and merges in a worktree, such as a slice into its
root branch, pass (AGENTS.md, Branches and Merges). It exits 2 with the reason on stderr to refuse, which the agent is shown, and 0 to let the command run; any other
command, or one it cannot place in a checkout, passes. Runs under pwsh and Windows PowerShell, from any directory.

.PARAMETER Change
What the change can affect, which picks what runs: Docs, Code or Visual.

.PARAMETER Hook
Check the git merge the hook's input on stdin names, rather than validate.

.EXAMPLE
tools/Scripts/AgentTaskComplete.ps1 -Change Code
#>
[CmdletBinding(DefaultParameterSetName = 'Validate')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Validate', Position = 0)]
    [ValidateSet('Docs', 'Code', 'Visual')]
    [string] $Change,
    [Parameter(Mandatory, ParameterSetName = 'Hook')]
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

# The commit a checkout's branch left its merge target at, or nothing when git finds none. The merge target is the
# branch's upstream when that is a local branch, as a swarm worker's root is, otherwise the branch checked out in the
# main checkout.
function Get-MergeBase([string] $checkout) {
    $ErrorActionPreference = 'Continue'
    try {
        $common = & git -C $checkout rev-parse --path-format=absolute --git-common-dir 2>$null
        $target = [string](& git -C $checkout rev-parse --symbolic-full-name '@{upstream}' 2>$null)
        if ($target -notmatch '^refs/heads/(.+)$') { $target = [string](Get-Content (Join-Path $common 'HEAD') -TotalCount 1) }
        $target = $target -replace '^(ref: )?refs/heads/', ''
        return [string](& git -C $checkout merge-base HEAD $target 2>$null)
    } finally {
        $ErrorActionPreference = 'Stop'
    }
}

# The files a change holds, against the merge base with its merge target, that make it more than a Docs change: each
# file that is not Markdown or a tool file, which Magic neither builds nor loads, unless it is a .cs or .slang file and every line the change adds or removes in it is a
# // comment or blank.
function Get-CodeChanges([string] $checkout) {
    $base = Get-MergeBase $checkout
    if (-not $base) { throw "git finds no merge base for $checkout." }
    $ErrorActionPreference = 'Continue'
    try {
        $new = @(& git -C $checkout ls-files --others --exclude-standard 2>$null)
        $changed = @(& git -C $checkout diff --name-only $base 2>$null) | Where-Object {
            if ($_ -notmatch '\.(cs|slang)$') { return $true }
            # The lines the diff adds or removes, without the two lines that name the file.
            $lines = @(& git -C $checkout diff -U0 $base -- $_ 2>$null) -match '^(\+(?!\+\+ )|-(?!-- ))'
            return @($lines | Where-Object { $_.Substring(1).Trim() -notmatch '^(//|$)' }).Count -gt 0
        }
        return @(@($new) + @($changed) | Where-Object { $_ -and $_ -notlike '*.md' -and $_ -notmatch '^(tools/Scripts|\.claude|\.githooks)/' })
    } finally {
        $ErrorActionPreference = 'Stop'
    }
}

# The stamp of a tree, as its level and its screenshots, or nothing when the tree has none. A stamp's first line is
# its level, the rest its screenshots; one written before stamps had levels holds only screenshots, from a run of
# every step.
function Get-Stamp([string] $checkout, [string] $tree) {
    $path = Get-StampPath $checkout $tree
    if (-not (Test-Path $path)) { return }
    $lines = @(Get-Content $path)
    if ($lines.Count -gt 0 -and $lines[0] -in 'Docs', 'Code', 'Visual') {
        return @{ Level = $lines[0]; Screenshots = @($lines | Select-Object -Skip 1) }
    }
    return @{ Level = 'Visual'; Screenshots = $lines }
}

# The stamps of the two sides of the pull that made a checkout's state, its own side first: when its last commit is a
# merge that git made with no conflict, nothing changed since, and both sides have a stamp. Nothing otherwise.
function Get-PulledStamps([string] $checkout, [string] $tree) {
    $ErrorActionPreference = 'Continue'
    try {
        $sides = @(& git -C $checkout rev-parse 'HEAD^1' 'HEAD^2' 2>$null)
        if ($LASTEXITCODE -ne 0 -or $sides.Count -ne 2) { return }
        $merged = @(& git -C $checkout merge-tree --write-tree $sides[0] $sides[1] 2>$null)
        if ($LASTEXITCODE -ne 0 -or $merged[0] -ne $tree) { return }
        $stamps = @($sides | ForEach-Object { Get-Stamp $checkout ([string](& git -C $checkout rev-parse "$_^{tree}")) })
        if ($stamps.Count -eq 2) { return $stamps }
    } finally {
        $ErrorActionPreference = 'Stop'
    }
}

# Writes the stamp of a checkout's state: its level, then its screenshots.
function Set-Stamp([string] $checkout, [string] $tree, [string[]] $lines) {
    $stamp = Get-StampPath $checkout $tree
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stamp) | Out-Null
    Set-Content -Path $stamp -Value $lines -Encoding utf8
}

# The sample projects a change can affect, when every file it changes against the merge base with its merge target,
# Markdown aside, is in the folder of one or more samples: those samples' projects. Nothing, which Validate.ps1 takes
# as every sample, otherwise.
function Get-AffectedProjects([string] $checkout) {
    $base = Get-MergeBase $checkout
    if (-not $base) { return @() }
    $ErrorActionPreference = 'Continue'
    try {
        $files = @(& git -C $checkout diff --name-only $base) + @(& git -C $checkout ls-files --others --exclude-standard)
    } finally {
        $ErrorActionPreference = 'Stop'
    }
    $folders = @()
    foreach ($file in $files) {
        if (-not $file -or $file -like '*.md') { continue }
        # Shared is the samples' shared gem, which every sample loads.
        if ($file -notmatch '^samples/([^/]+)/' -or $Matches[1] -eq 'Shared') { return @() }
        $folders += $Matches[1]
    }

    return @($folders | Sort-Object -Unique | ForEach-Object {
            Get-ChildItem (Join-Path (Join-Path $checkout 'samples') $_) -Filter '*.magic' | ForEach-Object { $_.FullName }
        })
}

# A Git Bash path (/c/Users/...) as Windows takes it.
function ConvertTo-Native([string] $path) {
    if ($path -match '^/([a-zA-Z])/(.*)$') { return "$($Matches[1]):/$($Matches[2])" }
    return $path
}

if (-not $Hook) {
    $root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    # The repository's hooks, which start a commit's subject with its type. The setting is the clone's, so
    # every worktree has them once one run set it.
    if (-not (& git -C $root config core.hooksPath)) { & git -C $root config core.hooksPath .githooks }
    # How thoroughly each level validates, and the Validate steps it runs.
    $rank = @{ Docs = 0; Code = 1; Visual = 2 }
    $steps = @{
        Code = @('Build', 'Test', 'Headless')
        Visual = @('Build', 'Test', 'Screenshot')
    }

    # A state stamped at this level or a higher one passed already.
    $tree = Get-CheckoutTree $root
    $stamped = Get-Stamp $root $tree
    if ($stamped -and $rank[$stamped.Level] -ge $rank[$Change]) {
        Write-Output "This state ($tree) was already validated as a $($stamped.Level) change: a git merge of it is allowed."
        foreach ($path in $stamped.Screenshots) { Write-Output "  $path" }
        exit 0
    }

    if ($Change -eq 'Docs') {
        $code = @(Get-CodeChanges $root)
        if ($code.Count -gt 0) {
            Write-Output "FAILED: a Docs change holds only Markdown, comments and tool files, and these files change more: $($code -join ', '). Run it with -Change Code or Visual."
            exit 1
        }
        Set-Stamp $root $tree @('Docs')
        Write-Output "Stamped this state ($tree) as a Docs change, with nothing run: a git merge of it is allowed."
        exit 0
    }

    $validate = @{ Only = $steps[$Change] }
    $carried = @()
    $pulled = @(Get-PulledStamps $root $tree)
    if ($pulled.Count -eq 2 -and $rank[$pulled[0].Level] -ge $rank[$Change]) {
        Write-Output 'This state is a pull of validated work into a validated state, with no conflict: only the build runs.'
        $validate.Only = @('Build')
        $carried = @($pulled[0].Screenshots)
    } else {
        & (Join-Path $PSScriptRoot 'Format.ps1')
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $projects = @(Get-AffectedProjects $root)
        if ($projects.Count -gt 0) {
            Write-Output "The change touches only these samples, so only they run: $($projects -join ', ')"
            $validate.Project = $projects
        }
    }

    $before = Get-CheckoutTree $root
    & (Join-Path $PSScriptRoot 'Validate.ps1') @validate | Tee-Object -Variable lines
    $code = $LASTEXITCODE
    if ($code -ne 0) { exit $code }

    $after = Get-CheckoutTree $root
    if ($after -ne $before) {
        Write-Output 'FAILED: files in the checkout changed while it was being validated, so what passed is not what is there: run it again.'
        exit 1
    }

    $screenshots = $carried + @($lines | Where-Object { $_ -like 'Screenshot: *' } | ForEach-Object { $_.Substring(12) })
    Set-Stamp $root $after (@($Change) + $screenshots)
    Write-Output ""
    Write-Output "Stamped this state ($after) as a $Change change: a git merge of it is allowed."
    if ($screenshots.Count -gt 0) {
        Write-Output 'Open each screenshot and look at what it shows before reporting the task complete:'
        foreach ($path in $screenshots) { Write-Output "  $path" }
    }
    exit 0
}

$hookInput = [Console]::In.ReadToEnd() | ConvertFrom-Json
$command = [string]$hookInput.tool_input.command
$here = [string]$hookInput.cwd

# merge as git's own command, not git merge-base.
if ($command -notmatch '\bgit\b(\s+-C\s+\S+)?\s+merge(?![-\w])') { exit 0 }

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
# Only the main checkout is checked: its git folder is the shared one.
$gitDir = & git -C $top rev-parse --path-format=absolute --git-dir
$common = & git -C $top rev-parse --path-format=absolute --git-common-dir
if ($gitDir -ne $common) { exit 0 }

# The branch merged: the first word after merge that is not an option. merge --abort or --continue names none.
$words = @(($command -split '\bmerge\b', 2)[1] -split '[\s;&|]+' | Where-Object { $_ -and -not $_.StartsWith('-') })
if ($words.Count -eq 0) { exit 0 }
$branch = $words[0].Trim('"', "'")
$tree = & git -C $top rev-parse --verify --quiet "$branch^{tree}"
if ($LASTEXITCODE -ne 0 -or -not $tree) { exit 0 }

if (Test-Path (Get-StampPath $top $tree)) { exit 0 }

[Console]::Error.WriteLine("$branch as its last commit has it was never validated: run tools/Scripts/AgentTaskComplete.ps1 -Change with what the change can affect in the worktree of $branch, open any screenshot it prints and look at it, then try again (AGENTS.md, Branches and Merges). An edit after the run, or a pull of new work, needs a new run.")
exit 2
