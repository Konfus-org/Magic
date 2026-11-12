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
    # Ask for a tidy only when there is something to tidy: a stale claim, a worktree whose folder is gone, or one whose
    # branch merged into the merge target, which has moved on since. A branch with no commit of its own, as its reflog
    # shows, is a slice that just started, so it does not count.
    $finished = 0
    if ($checkout.Target) {
        $merged = @(Invoke-Git $checkout.Top @('branch', '--merged', $checkout.Target, '--format=%(refname:short)'))
        $targetHead = [string](Invoke-Git $checkout.Top @('rev-parse', $checkout.Target))
        $path = $null
        foreach ($line in @(Invoke-Git $checkout.Top @('worktree', 'list', '--porcelain'))) {
            if ($line -like 'worktree *') { $path = $line.Substring(9) }
            if ($line -match '^branch refs/heads/(.+)$' -and $Matches[1] -ne $checkout.Target -and
                (ConvertTo-Key $path) -ne (ConvertTo-Key $checkout.Top)) {
                $branch = $Matches[1]
                $gone = -not (Test-Path -LiteralPath $path)
                $done = $merged -contains $branch -and
                    [string](Invoke-Git $checkout.Top @('rev-parse', $branch)) -ne $targetHead -and
                    @(Invoke-Git $checkout.Top @('reflog', 'show', '--format=%gs', "refs/heads/$branch")).Count -gt 1
                if ($gone -or $done) { $finished++ }
            }
        }
    }
    $out.Add('- A clear request starts after a short brief (the slice skill); a new effort too big for one slice starts with the kickoff skill; a list of separate tasks, or a feature that splits into slices that can run side by side, runs as a swarm (the parallelize skill).')
    $out.Add('- git commit needs AgentTaskComplete and one slice-reviewer review first; a slice of 50 lines or fewer, a Docs change and a swarm''s coder need no review.')

    # Every slice in progress, from tools/Scripts/Claim.ps1, so this session works around them.
    $claims = @(Get-ChildItem -LiteralPath (Join-Path $checkout.Common 'agent-claims') -File -ErrorAction SilentlyContinue)
    $staleCount = @($claims | Where-Object { -not (Test-ClaimLive (Read-Claim $_.FullName)) }).Count
    if ($finished -gt 0 -or $staleCount -gt 0) {
        $out.Insert(2, "- Tidy first: $finished finished worktree(s) and $staleCount stale claim(s) (the tidy-worktrees skill).")
    }
    if ($claims.Count -gt 0) {
        $out.Add('- Slices in progress and the paths each claims; never edit inside one without the user''s yes (AGENTS.md, Worktrees):')
        foreach ($file in $claims) {
            $claim = Read-Claim $file.FullName
            $stale = if (Test-ClaimLive $claim) { '' } else { ' [stale: its worktree is gone]' }
            $out.Add("  - $($claim.branch): $($claim.slice) ($($claim.scope -join ', '))$stale")
        }
    }
    [Console]::Out.Write(($out -join "`n"))
} catch {
    exit 0
}
exit 0
