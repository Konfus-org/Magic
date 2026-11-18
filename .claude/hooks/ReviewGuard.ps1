# PreToolUse on git: a git merge in the main checkout needs the qa subagent's last verdict on the merged branch to be
# PASS. Each branch needs it, small or Docs alone. Commits, and merges in a worktree, such as a slice into its root
# branch, are not checked (docs/skills/PROJECT.md, Branches and Merges).
# Exits 2 with the reason on stderr to refuse, which the agent is shown.
. (Join-Path $PSScriptRoot 'common.ps1')

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

    # The stamp counts only when QA checked a commit of this branch: an old stamp of a branch with the same name does not.
    $stamp = Get-ReviewStamp $checkout $branch
    $lines = @(if (Test-Path -LiteralPath $stamp) { Get-Content -LiteralPath $stamp })
    $checked = if ($lines.Count -gt 0) { $lines[0].Trim([char]0xFEFF, ' ') } else { '' }
    $common = if ($checked) { [string](Invoke-Git $checkout.Top @('merge-base', $checked, $branch)) } else { '' }
    if (-not $checked -or $common -ne $checked) {
        [Console]::Error.WriteLine("$branch has no QA: do the cleanup branch pass, then run the qa subagent in its worktree in the foreground (the finish skill, step 2).")
        exit 2
    }
    if ((Get-Verdict ($lines -join "`n")) -ne 'PASS') {
        [Console]::Error.WriteLine("QA of $branch did not pass: fix each REJECT and each critical and high issue at its cause, then run the qa subagent again (the finish skill, step 2).")
        exit 2
    }
    exit 0
} catch {
    exit 0
}
exit 0
