# PreToolUse on git: a git merge in the main checkout needs the slice-reviewer to have reviewed the merged branch.
# Commits, and merges in a worktree, such as a slice into its root branch, are not checked (AGENTS.md, Branches and
# Merges). A small branch needs no review either: one of $Small changed lines or fewer, or one validated as a Docs change.
# Exits 2 with the reason on stderr to refuse, which the agent is shown.
. (Join-Path $PSScriptRoot 'common.ps1')
$Small = 50

try {
    $hook = Read-HookInput
    $command = [string](Get-Field (Get-Field $hook 'tool_input') 'command')
    if ($command -notmatch '\bgit\b(\s+-C\s+\S+)?\s+merge(?![-\w])') { exit 0 }

    $folder = [string](Get-Field $hook 'cwd')
    if ($command -match 'git\s+-C\s+(?:"([^"]+)"|''([^'']+)''|(\S+))') {
        $folder = @($Matches[1], $Matches[2], $Matches[3]) | Where-Object { $_ } | Select-Object -First 1
    } elseif ($command -match '\bcd\s+(?:"([^"]+)"|''([^'']+)''|(\S+))') {
        $folder = @($Matches[1], $Matches[2], $Matches[3]) | Where-Object { $_ } | Select-Object -First 1
    }
    if ($folder -match '^/([a-zA-Z])/(.*)$') { $folder = "$($Matches[1]):/$($Matches[2])" }
    $checkout = Get-Checkout $folder
    if (-not $checkout -or -not (Test-ThisRepository $checkout) -or $checkout.IsWorktree) { exit 0 }

    # The branch merged: the first word after merge that is not an option. merge --abort or --continue names none.
    $words = @(($command -split '\bmerge\b', 2)[1] -split '[\s;&|]+' | Where-Object { $_ -and -not $_.StartsWith('-') })
    if ($words.Count -eq 0) { exit 0 }
    $branch = $words[0].Trim('"', "'")
    if (-not (Invoke-Git $checkout.Top @('rev-parse', '--verify', '--quiet', "refs/heads/$branch"))) { exit 0 }
    if (Test-Path -LiteralPath (Get-ReviewStamp $checkout $branch)) { exit 0 }

    # Small: a merge base and $Small changed lines or fewer against it, or a last commit validated as a Docs change.
    $base = [string](Invoke-Git $checkout.Top @('merge-base', 'HEAD', $branch))
    $lines = if ($base) { Measure-ChangedLines $checkout.Top @('diff', '--numstat', $base, $branch) } else { 0 }
    if ($base -and $lines -le $Small) { exit 0 }
    $tree = [string](Invoke-Git $checkout.Top @('rev-parse', "$branch^{tree}"))
    $stamp = Get-ValidationStamp $checkout $tree
    if ((Test-Path -LiteralPath $stamp) -and [string](Get-Content -LiteralPath $stamp -TotalCount 1) -eq 'Docs') { exit 0 }

    [Console]::Error.WriteLine("$branch was never reviewed: have the slice-reviewer subagent review it in its worktree first (AGENTS.md, Branches and Merges).")
    exit 2
} catch {
    exit 0
}
exit 0
