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
    $out.Add('- A clear request starts after a short brief (the slice skill); a new effort too big for one slice starts with the kickoff skill.')
    $out.Add('- git commit needs AgentTaskComplete and one slice-reviewer review first.')
    [Console]::Out.Write(($out -join "`n"))
} catch {
    exit 0
}
exit 0
