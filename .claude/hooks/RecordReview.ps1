# SubagentStop, and PostToolUse on SubagentHandback: when the qa subagent finishes with a verdict, records it for the
# branch, with the commit that it checked. ReviewGuard.ps1 lets the branch merge when that verdict is PASS. QA in the
# foreground ends with its report; QA in the background hands its report back through the SubagentHandback tool. QA
# names the worktree it checked on a WORKTREE: line. The stamp sits beside AgentTaskComplete's in the git folder.
# A verdict it can't record exits 1 with the reason on stderr, which the user is shown.
. (Join-Path $PSScriptRoot 'common.ps1')
try {
    $hook = Read-HookInput
    if ([string](Get-Field $hook 'agent_type') -ne 'qa') { exit 0 }
    $review = [string](Get-Field $hook 'last_assistant_message')
    if (-not $review) { $review = [string](Get-Field (Get-Field $hook 'tool_input') 'message') }
    if (-not (Get-Verdict $review)) { exit 0 }

    $path = [string](Get-Field $hook 'cwd')
    if ($review -match '(?m)^\s*WORKTREE:\s*(.+?)\s*$') { $path = $Matches[1].Trim('`', '"', "'") }
    $checkout = Get-Checkout $path
    if (-not $checkout -or -not $checkout.IsWorktree -or -not (Test-ThisRepository $checkout)) {
        throw "the review names $path, which is no worktree of this repository"
    }
    $branch = [string](Invoke-Git $checkout.Top @('branch', '--show-current'))
    if (-not $branch) { throw "the worktree $path has no branch checked out" }
    $commit = [string](Invoke-Git $checkout.Top @('rev-parse', 'HEAD'))
    $stamp = Get-ReviewStamp $checkout $branch
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stamp) | Out-Null
    Set-Content -LiteralPath $stamp -Value (@($commit) + $review) -Encoding utf8
} catch {
    [Console]::Error.WriteLine("No QA verdict was recorded: $_.")
    exit 1
}
exit 0
