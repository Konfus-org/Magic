# PreToolUse on git: a git commit in a worktree needs the slice-reviewer to have reviewed its branch, and a new review
# once the slice grew by more than $Regrowth lines since; a git merge needs the merged branch reviewed.
# Exits 2 with the reason on stderr to refuse, which the agent is shown.
. (Join-Path $PSScriptRoot 'common.ps1')
$Regrowth = 100
try {
    $hook = Read-HookInput
    $command = [string](Get-Field (Get-Field $hook 'tool_input') 'command')
    if ($command -notmatch '\bgit\b(\s+-C\s+\S+)?\s+(commit|merge)(?![-\w])') { exit 0 }
    $isMerge = $Matches[2] -eq 'merge'

    $folder = [string](Get-Field $hook 'cwd')
    if ($command -match 'git\s+-C\s+(?:"([^"]+)"|''([^'']+)''|(\S+))') {
        $folder = @($Matches[1], $Matches[2], $Matches[3]) | Where-Object { $_ } | Select-Object -First 1
    } elseif ($command -match '\bcd\s+(?:"([^"]+)"|''([^'']+)''|(\S+))') {
        $folder = @($Matches[1], $Matches[2], $Matches[3]) | Where-Object { $_ } | Select-Object -First 1
    }
    if ($folder -match '^/([a-zA-Z])/(.*)$') { $folder = "$($Matches[1]):/$($Matches[2])" }
    $checkout = Get-Checkout $folder
    if (-not $checkout -or -not (Test-ThisRepository $checkout)) { exit 0 }

    if ($isMerge) {
        $words = @(($command -split '\bmerge\b', 2)[1] -split '[\s;&|]+' | Where-Object { $_ -and -not $_.StartsWith('-') })
        if ($words.Count -eq 0) { exit 0 }
        $branch = $words[0].Trim('"', "'")
        if (-not (Invoke-Git $checkout.Top @('rev-parse', '--verify', '--quiet', "refs/heads/$branch"))) { exit 0 }
        if (Test-Path -LiteralPath (Get-ReviewStamp $checkout $branch)) { exit 0 }
        [Console]::Error.WriteLine("$branch was never reviewed: have the slice-reviewer subagent review it in its worktree first (AGENTS.md, Finish).")
        exit 2
    }

    if (-not $checkout.IsWorktree) { exit 0 }
    $branch = [string](Invoke-Git $checkout.Top @('branch', '--show-current'))
    if (-not $branch) { exit 0 }
    $stamp = Get-ReviewStamp $checkout $branch
    if (-not (Test-Path -LiteralPath $stamp)) {
        [Console]::Error.WriteLine("This slice ($branch) has no review: have the slice-reviewer subagent review it, naming the worktree, the merge target $($checkout.Target) and the slice, then commit (AGENTS.md, Finish).")
        exit 2
    }
    $reviewed = 0
    [int]::TryParse([string](Get-Content -LiteralPath $stamp -TotalCount 1), [ref]$reviewed) | Out-Null
    $now = (Get-SliceState $checkout).Lines
    if ($now -gt $reviewed + $Regrowth) {
        [Console]::Error.WriteLine("This slice grew from $reviewed to $now changed lines since its review, past the $Regrowth a fix may add without one: have the slice-reviewer review it again, then commit.")
        exit 2
    }
} catch {
    exit 0
}
exit 0
