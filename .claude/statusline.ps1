# Status line: the checkout, the slice's size against the merge target, the model and how full the context is.
$ErrorActionPreference = 'SilentlyContinue'
$data = [Console]::In.ReadToEnd() | ConvertFrom-Json
$esc = [char]27
$dir = $data.workspace.current_dir
if (-not $dir) { $dir = $data.cwd }

$branch = & git -C $dir branch --show-current 2>$null
$gitDir = & git -C $dir rev-parse --path-format=absolute --git-dir 2>$null
$common = & git -C $dir rev-parse --path-format=absolute --git-common-dir 2>$null
$parts = New-Object System.Collections.Generic.List[string]
if ($gitDir -and $common -and $gitDir -ne $common) {
    $parts.Add("$esc[36m$branch$esc[0m")
    $main = (& git -C $dir worktree list --porcelain 2>$null | Where-Object { $_ -like 'branch refs/heads/*' } | Select-Object -First 1)
    if ($main) {
        $target = $main.Substring(18)
        $base = & git -C $dir merge-base HEAD $target 2>$null
        if ($base) {
            $lines = 0
            foreach ($row in (& git -C $dir diff --numstat $base 2>$null)) {
                $cells = $row -split "`t"
                if ($cells[0] -match '^\d+$') { $lines += [int]$cells[0] + [int]$cells[1] }
            }
            $color = if ($lines -gt 300) { '31' } elseif ($lines -gt 200) { '33' } else { '32' }
            $parts.Add("$esc[${color}mslice $lines/300$esc[0m")
        }
    }
} elseif ($branch) {
    $parts.Add("$esc[35mmain checkout ($branch)$esc[0m")
}

$parts.Add($data.model.display_name)
$used = $data.context_window.used_percentage
if ($null -ne $used) {
    $pct = [int][math]::Round([double]$used)
    if ($pct -ge 60) { $parts.Add("$esc[31mctx $pct% - finish the slice, then /clear$esc[0m") }
    elseif ($pct -ge 40) { $parts.Add("$esc[33mctx $pct%$esc[0m") }
    else { $parts.Add("ctx $pct%") }
}
[Console]::Out.Write(($parts -join '  |  '))
