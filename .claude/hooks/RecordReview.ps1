# SubagentStop: when the slice-reviewer finishes with a verdict, stamps the slice's branch as reviewed, with its size
# then, so ReviewGuard.ps1 lets it commit. The reviewer names the worktree it reviewed on a WORKTREE: line. The stamp
# sits beside AgentTaskComplete's in the repository's git folder.
. (Join-Path $PSScriptRoot 'common.ps1')
try {
    $hook = Read-HookInput
    if ([string](Get-Field $hook 'agent_type') -ne 'slice-reviewer') { exit 0 }
    $review = [string](Get-Field $hook 'last_assistant_message')
    if ($review -notmatch 'VERDICT:\s*(PASS|FINDINGS)') { exit 0 }

    $path = [string](Get-Field $hook 'cwd')
    if ($review -match '(?m)^\s*WORKTREE:\s*(.+?)\s*$') { $path = $Matches[1].Trim('`', '"', "'") }
    $checkout = Get-Checkout $path
    if (-not $checkout -or -not $checkout.IsWorktree -or -not (Test-ThisRepository $checkout)) { exit 0 }
    $branch = [string](Invoke-Git $checkout.Top @('branch', '--show-current'))
    if (-not $branch) { exit 0 }
    $state = Get-SliceState $checkout
    $stamp = Get-ReviewStamp $checkout $branch
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stamp) | Out-Null
    Set-Content -LiteralPath $stamp -Value (@([string]$state.Lines) + $review) -Encoding utf8
} catch {
    exit 0
}
exit 0
