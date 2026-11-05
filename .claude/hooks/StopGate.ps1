# Stop: a turn that leaves changes in a worktree cannot end until AgentTaskComplete stamped that exact state and the
# slice-reviewer subagent reviewed it. A turn that stops to ask the user or report a block says NEEDS-USER: and passes.
. (Join-Path $PSScriptRoot 'common.ps1')
try {
    $hook = Read-HookInput
    if ([string](Get-Field $hook 'permission_mode') -eq 'plan') { exit 0 }
    if ([string](Get-Field $hook 'last_assistant_message') -match 'NEEDS-USER:') { exit 0 }

    $checkout = Resolve-SliceCheckout $hook
    if (-not $checkout -or -not $checkout.Target -or -not (Test-ThisRepository $checkout)) { exit 0 }
    $state = Get-SliceState $checkout
    if (-not $state.BaseTree -or $state.Tree -eq $state.BaseTree) { exit 0 }

    $where = "the worktree $($checkout.Top) ($($state.Lines) changed lines against $($checkout.Target))"
    if (-not (Test-Path -LiteralPath (Get-ValidationStamp $checkout $state.Tree))) {
        $reason = "The changes in $where were never validated in this state. Run tools/Scripts/AgentTaskComplete.ps1 " +
            "-Change Docs, Code or Visual there, whichever covers the change, and look at any screenshot it prints " +
            "(AGENTS.md, Finish). If you are stopping to ask the user or to report a block, say so in a line starting with NEEDS-USER:."
    } elseif (-not (Test-Path -LiteralPath (Get-ReviewStamp $checkout $state.Tree))) {
        $reason = "The changes in $where are validated but not reviewed. Use the Agent tool with subagent_type " +
            "slice-reviewer, naming the worktree path, the merge target $($checkout.Target) and the PLAN.md slice. Fix " +
            "its findings at their cause or say with evidence why not; a fix needs AgentTaskComplete again and then a new review."
    } else {
        exit 0
    }
    Write-HookJson @{ decision = 'block'; reason = $reason }
} catch {
    exit 0
}
exit 0
