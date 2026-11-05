# SubagentStop: when the slice-reviewer finishes with a verdict, stamps the reviewed state so the Stop hook lets the
# turn end. The stamp holds the review, beside AgentTaskComplete's stamps in the repository's git folder.
. (Join-Path $PSScriptRoot 'common.ps1')
try {
    $hook = Read-HookInput
    if ([string](Get-Field $hook 'agent_type') -ne 'slice-reviewer') { exit 0 }
    $review = [string](Get-Field $hook 'last_assistant_message')
    if ($review -notmatch 'VERDICT:\s*(PASS|FINDINGS)') { exit 0 }

    $checkout = Resolve-SliceCheckout $hook
    if (-not $checkout -or -not (Test-ThisRepository $checkout)) { exit 0 }
    $state = Get-SliceState $checkout
    $stamp = Get-ReviewStamp $checkout $state.Tree
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stamp) | Out-Null
    Set-Content -LiteralPath $stamp -Value $review -Encoding utf8
} catch {
    exit 0
}
exit 0
