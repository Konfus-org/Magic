# PreToolUse on file edits: refuses an edit to a file in the main checkout (AGENTS.md, Worktrees).
# MAGIC_ALLOW_MAIN_EDITS=1 in the environment Claude Code starts in turns it off for that session.
. (Join-Path $PSScriptRoot 'common.ps1')
try {
    if ($env:MAGIC_ALLOW_MAIN_EDITS -eq '1') { exit 0 }
    $hook = Read-HookInput
    $toolInput = Get-Field $hook 'tool_input'
    $path = [string](Get-Field $toolInput 'file_path')
    if (-not $path) { $path = [string](Get-Field $toolInput 'notebook_path') }
    $checkout = Get-Checkout $path
    if (-not $checkout -or $checkout.IsWorktree -or -not (Test-ThisRepository $checkout)) { exit 0 }

    $reason = "$path is in the main checkout, where agents never edit (AGENTS.md, Worktrees). Make the task's worktree " +
        "first, for example: git worktree add ../Magic-worktrees/<type>/<feature> -b <type>/<feature>, then edit the " +
        "file there. The user can allow main-checkout edits for a session by starting Claude Code with MAGIC_ALLOW_MAIN_EDITS=1."
    Write-HookJson @{
        hookSpecificOutput = @{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'deny'
            permissionDecisionReason = $reason
        }
    }
} catch {
    exit 0
}
exit 0
