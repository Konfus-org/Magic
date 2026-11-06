# Status line: the checkout, the slice's size against the merge target, the model and how full the context is.
# Three git calls at most, since it runs often.
$ErrorActionPreference = 'SilentlyContinue'
$data = [Console]::In.ReadToEnd() | ConvertFrom-Json
$esc = [char]27
$dir = $data.workspace.current_dir
if (-not $dir) { $dir = $data.cwd }

$parts = New-Object System.Collections.Generic.List[string]
$info = @(& git -C $dir rev-parse --path-format=absolute --git-dir --git-common-dir --abbrev-ref HEAD 2>$null)
if ($info.Count -ge 3) {
    $branch = $info[2]
    if ($info[0] -ne $info[1]) {
        $parts.Add("$esc[36m$branch$esc[0m")
        $ref = Get-Content -LiteralPath (Join-Path $info[1] 'HEAD') -TotalCount 1
        if ($ref -match '^ref: refs/heads/(.+)$') {
            $base = & git -C $dir merge-base HEAD $Matches[1] 2>$null
            if ($base) {
                $lines = 0
                $stat = [string](& git -C $dir diff --shortstat $base 2>$null)
                foreach ($m in [regex]::Matches($stat, '(\d+) (insertion|deletion)')) { $lines += [int]$m.Groups[1].Value }
                $color = if ($lines -gt 300) { '31' } elseif ($lines -gt 200) { '33' } else { '32' }
                $parts.Add("$esc[${color}mslice $lines/300$esc[0m")
            }
        }
    } else {
        $parts.Add("$esc[35mmain checkout ($branch)$esc[0m")
    }
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
