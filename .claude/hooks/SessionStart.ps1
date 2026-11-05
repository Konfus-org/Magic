# SessionStart: puts the checkout and PLAN.md's state in Claude's context, so the workflow starts without a command.
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

    $plan = Join-Path $checkout.Top 'PLAN.md'
    if (-not (Test-Path -LiteralPath $plan)) {
        $out.Add('- There is no PLAN.md. Run the kickoff interview (the kickoff skill) before any other work.')
    } else {
        $section = ''
        $goalOpen = $false
        $inCurrent = $false
        $bucket = $null
        $next = $null
        foreach ($line in Get-Content -LiteralPath $plan) {
            if ($line -match '^## (.+)$') { $section = $Matches[1].Trim(); $inCurrent = $false; continue }
            if ($line -match '^### (.+)$') {
                $inCurrent = $Matches[1] -like 'Current*'
                if ($inCurrent) { $bucket = ($Matches[1] -replace '^Current:\s*', '').Trim() }
                continue
            }
            if ($section -eq 'Goal' -and $line -match 'TODO') { $goalOpen = $true }
            if ($inCurrent -and -not $next -and $line -match '^\s*- \[ \] (.+)$') { $next = $Matches[1].Trim() }
        }
        if ($goalOpen) {
            $out.Add('- PLAN.md has no goal yet. Before any other work, run the kickoff interview (the kickoff skill), one question at a time.')
        } else {
            $out.Add("- Current bucket: $bucket")
            if ($next) { $out.Add("- Next slice: $next") }
            else { $out.Add('- The current bucket has no open slice: propose the next bucket (the kickoff skill).') }
            $out.Add('- Map any request for work to a PLAN.md slice; the slice skill says when it needs a proposal first.')
        }
    }
    $out.Add('- A slice approved in PLAN.md starts without a new proposal. git commit needs AgentTaskComplete and one slice-reviewer review first.')
    [Console]::Out.Write(($out -join "`n"))
} catch {
    exit 0
}
exit 0
