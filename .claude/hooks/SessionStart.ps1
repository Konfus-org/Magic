# SessionStart: puts the checkout and its merge target in Claude's context, so the workflow starts without a command.
. (Join-Path $PSScriptRoot 'common.ps1')
try {
    $hook = Read-HookInput
    $cwd = [string](Get-Field $hook 'cwd')
    if (-not $cwd) { $cwd = $env:CLAUDE_PROJECT_DIR }
    $checkout = Get-Checkout $cwd
    if (-not $checkout) { exit 0 }

    $out = New-Object System.Collections.Generic.List[string]
    $out.Add('Magic workflow state, from the SessionStart hook:')
    if ($checkout.IsWorktree) {
        $out.Add("- Worktree: $($checkout.Top). Merge target: $($checkout.Target).")
    } else {
        $out.Add("- Main checkout, on $($checkout.Target). Edits here are refused: make a worktree before the first edit (AGENTS.md, Worktrees).")
    }
    # Ask for a tidy only when there is something to tidy: a worktree whose folder is gone, or one whose branch merged
    # into the merge target and that has no change that is not committed.
    $finished = 0
    if ($checkout.Target) {
        $path = $null
        foreach ($line in @(Invoke-Git $checkout.Top @('worktree', 'list', '--porcelain'))) {
            if ($line -like 'worktree *') { $path = $line.Substring(9) }
            if ($line -match '^branch refs/heads/(.+)$' -and $Matches[1] -ne $checkout.Target -and
                (ConvertTo-Key $path) -ne (ConvertTo-Key $checkout.Top)) {
                $gone = -not (Test-Path -LiteralPath $path)
                if ($gone -or (Test-BranchMerged $path $Matches[1] $checkout.Target)) { $finished++ }
            }
        }
    }
    $out.Add('- A request starts when the user agrees to its brief and plan; a small change, or a slice of an agreed plan, starts after its brief (the slice skill). A new effort too big for one slice starts with the kickoff skill. A swarm runs only when the user asks for one, or at four or more slices that do not depend on each other (the parallelize skill).')
    $out.Add('- Hard rule: an effort of more than one slice has one root branch; its slices go into it, and it merges into the main checkout''s branch one time, at the end, after the user''s yes (AGENTS.md, Branches and Merges).')
    $out.Add('- A git merge in the main checkout needs AgentTaskComplete and one slice-reviewer review first; a branch of 50 lines or fewer, or a Docs change, needs no review. Commits are free.')

    if ($finished -gt 0) {
        $out.Insert(2, "- Tidy first: $finished finished worktree(s) (the tidy-worktrees skill).")
    }
    [Console]::Out.Write(($out -join "`n"))
} catch {
    exit 0
}
exit 0
