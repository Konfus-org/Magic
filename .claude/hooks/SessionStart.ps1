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
    $out.Add('- Tidy finished worktrees and stale claims first (the tidy-worktrees skill).')
    $out.Add('- A clear request starts after a short brief (the slice skill); a new effort too big for one slice starts with the kickoff skill; a list of separate tasks, or a feature that splits into slices that can run side by side, runs as a swarm (the parallelize skill).')
    $out.Add('- git commit needs AgentTaskComplete and one slice-reviewer review first.')

    # Every slice in progress, from tools/Scripts/Claim.ps1, so this session works around them.
    $claims = @(Get-ChildItem -LiteralPath (Join-Path $checkout.Common 'agent-claims') -File -ErrorAction SilentlyContinue)
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
