# Status line: the checkout, the model and how full the context is. One git call, since it runs often.
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
