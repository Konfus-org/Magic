# PostToolUse on file edits: records the worktree this session works in, and warns once the slice passes its size.
. (Join-Path $PSScriptRoot 'common.ps1')
$Limit = 300
try {
    $hook = Read-HookInput
    $toolInput = Get-Field $hook 'tool_input'
    $path = [string](Get-Field $toolInput 'file_path')
    if (-not $path) { $path = [string](Get-Field $toolInput 'notebook_path') }
    $checkout = Get-Checkout $path
    if (-not $checkout -or -not $checkout.IsWorktree -or -not (Test-ThisRepository $checkout)) { exit 0 }

    $record = Get-SessionRecord $checkout.Common ([string](Get-Field $hook 'session_id'))
    if ($record) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $record) | Out-Null
        Set-Content -LiteralPath $record -Value $checkout.Top -Encoding ascii
    }

    $state = Get-SliceState $checkout
    if ($state.Lines -le $Limit) { exit 0 }
    $message = "Slice size: $($state.Lines) changed lines against $($checkout.Target), past the limit of about $Limit " +
        "(AGENTS.md, Slices). Stop adding to it: report what exists in a line starting with NEEDS-USER:, and let the " +
        "user continue, split or drop the slice."
    Write-HookJson @{ hookSpecificOutput = @{ hookEventName = 'PostToolUse'; additionalContext = $message } }
} catch {
    exit 0
}
exit 0
