<#
.SYNOPSIS
Judges the rows of a run of VisualTests.ps1 against the limits of VisualTests.json and the baseline, prints a table of
each view and hour, and writes the report page.

.DESCRIPTION
VisualTests.ps1 writes rows.json beside the folders of its run and calls this script at its end. Run it by hand on the
same file after a change to a limit or a tolerance, and the same run is judged again with no run of Magic:

    tools/Scripts/JudgeVisualTests.ps1 -Run <the folder of a run>

rows.json holds the tier, the jobs of the run with the pictures each measure wrote, and every row as
{ job, run, name, limit, value, note }, where the limit is a key under `limits` in VisualTests.json. A row whose key is
not there prints with "no limit yet" and fails nothing. The GPU time of a frame is one of those.

A baseline under tools/Reference/Baseline holds each row's value from a run that the user approved. While one is there,
each row is compared with it and prints a second verdict: WORSE when a failing row moved away from its limit by more
than the row's tolerance, BETTER when it moved toward it, SAME else. The tolerances are in VisualTests.json. The
command ends with 0 when every row passes, 2 when rows fail but none is WORSE, and 1 when a row is WORSE.

With rows of more than one run of each view and hour, from VisualTests.ps1 -Repeat, it prints the spread of each row
instead of judging a baseline, which is what the tolerances are set from.

.PARAMETER Run
The rows.json of a run, or the folder that holds it.

.PARAMETER Out
The report page to write. report.html beside rows.json when not given.

.PARAMETER UpdateBaseline
Writes each row's value to tools/Reference/Baseline, which the runs after it compare with.

.EXAMPLE
tools/Scripts/JudgeVisualTests.ps1 -Run C:/Users/me/AppData/Local/Magic/Screenshots/Reference
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Run,
    [string] $Out,
    [switch] $UpdateBaseline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scripts = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scripts 'ReferenceLib.ps1')

$rowsPath = if (Test-Path $Run -PathType Container) { Join-Path $Run 'rows.json' } else { $Run }
if (-not (Test-Path $rowsPath)) { throw "No rows of a run at ${rowsPath}: VisualTests.ps1 writes it." }
if (-not $Out) { $Out = Join-Path (Split-Path -Parent (Resolve-Path $rowsPath)) 'report.html' }
$settings = Read-Json (Join-Path $scripts 'VisualTests.json')
$written = Read-Json $rowsPath
$runs = @($written.rows | ForEach-Object { $_.run } | Sort-Object -Unique)

# Each row with the limit it is judged by: the least and the most it may be, the text the table prints, whether it
# passes, and how far outside its limit it lies, which a baseline verdict is the change in.
$rows = @($written.rows | ForEach-Object {
    $range = Get-LimitRange $settings.limits $_.limit
    $note = if ($_.note) { $_.note } elseif ($settings.proposed -contains $_.limit) { 'proposed' } else { '' }
    [pscustomobject]@{
        Job = $_.job; Run = $_.run; Name = $_.name; Key = $_.limit; Number = [double] $_.value; Note = $note
        Value = [string]::Format($script:Invariant, '{0:0.###}', [double] $_.value)
        Limit = if ($range) { $range.Text } else { 'no limit yet' }
        Pass = if ($range) { [double] $_.value -ge $range.Least -and [double] $_.value -le $range.Most } else { $null }
        Range = $range
    }
})
$judged = @($rows | Where-Object { $_.Run -eq $runs[0] })

if ($UpdateBaseline) {
    foreach ($job in $written.jobs) {
        $kept = [ordered]@{}
        foreach ($row in $judged | Where-Object { $_.Job -eq $job.name } | Sort-Object Name) {
            $kept[$row.Name] = [double] [string]::Format($script:Invariant, '{0:0.######}', $row.Number)
        }
        $path = (Get-ReferencePaths $job.view $job.hour).Baseline
        Write-Json $path $kept 3
        Write-Output "baseline   $path"
    }
}

# The verdict of each row against the baseline, by the view and hour it belongs to and its name.
$verdicts = @{}
if (-not $UpdateBaseline -and $runs.Count -eq 1) {
    foreach ($job in $written.jobs) {
        $path = (Get-ReferencePaths $job.view $job.hour).Baseline
        if (-not (Test-Path $path)) { continue }
        $baseline = Read-Json $path
        foreach ($row in $judged | Where-Object { $_.Job -eq $job.name }) {
            if (-not (Test-Field $baseline $row.Name)) { continue }
            $was = [double] $baseline.($row.Name)
            $tolerance = if (Test-Field $settings.tolerances $row.Key) { [double] $settings.tolerances.($row.Key) }
                else { [double] $settings.tolerances.default }
            $allowed = $tolerance * [Math]::Max([Math]::Abs($was), 1e-9)
            $moved = (Get-Outside $row.Number $row.Range) - (Get-Outside $was $row.Range)
            $verdicts["$($row.Job)|$($row.Name)"] = if ($moved -gt $allowed) { 'WORSE' }
                elseif ($moved -lt -$allowed) { 'BETTER' } else { 'SAME' }
        }
    }
}

# The verdict against the baseline of one row, which is empty where no baseline holds it.
function Get-Verdict([object] $Row) {
    $key = "$($Row.Job)|$($Row.Name)"
    if ($verdicts.ContainsKey($key)) { return $verdicts[$key] }
    return ''
}

$format = '{0,-42} {1,12}  {2,-16} {3,-5} {4,-7} {5}'
foreach ($job in $written.jobs) {
    Write-Output ''
    Write-Output "== $($job.name)$(if ($job.stale) { '  STALE: its reference was rendered from another scene' })"
    Write-Output ($format -f 'measurement', 'value', 'limit', '', '', 'note')
    foreach ($row in $judged | Where-Object { $_.Job -eq $job.name }) {
        $pass = if ($null -eq $row.Pass) { '' } elseif ($row.Pass) { 'PASS' } else { 'FAIL' }
        Write-Output ($format -f $row.Name, $row.Value, $row.Limit, $pass, (Get-Verdict $row), $row.Note)
    }
}

Write-Output ''
Write-Output ('The tolerances of these rows are proposals from two runs, not values the user has set: ' +
    ($settings.proposedTolerances -join ', ') + '.')

if ($runs.Count -gt 1) {
    Write-Output ''
    Write-Output "== the spread over $($runs.Count) runs, which is what each row's tolerance is set from"
    Write-Output ('{0,-46} {1,-28} {2,10} {3,12}' -f 'measurement', 'values', 'spread', 'of the mean')
    foreach ($job in $written.jobs) {
        Write-Output "-- $($job.name)"
        foreach ($name in @($judged | Where-Object { $_.Job -eq $job.name } | ForEach-Object { $_.Name })) {
            $values = @($rows | Where-Object { $_.Job -eq $job.name -and $_.Name -eq $name } |
                ForEach-Object { $_.Number })
            $measure = $values | Measure-Object -Minimum -Maximum -Average
            $spread = $measure.Maximum - $measure.Minimum
            $part = $spread / [Math]::Max([Math]::Abs($measure.Average), 1e-9)
            Write-Output ('{0,-46} {1,-28} {2,10} {3,12}' -f $name,
                (($values | ForEach-Object { [string]::Format($script:Invariant, '{0:0.###}', $_) }) -join ' '),
                [string]::Format($script:Invariant, '{0:0.####}', $spread),
                [string]::Format($script:Invariant, '{0:0.##%}', $part))
        }
    }
}

# The report page, which holds each view and hour's table and the pictures that its measures wrote, the failing rows
# first. It asks nothing of the network and runs no script, so it opens from the file alone.
function Get-Escaped([string] $Text) {
    return $Text.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;')
}

# The path of a file of a view's folder, relative to the page's own folder, or nothing when the file is not there.
function Get-Relative([string] $Folder, [string] $Name, [string] $PageFolder) {
    $path = Join-Path $Folder $Name
    if (-not (Test-Path $path)) { return $null }
    return ([Uri]($PageFolder + '/')).MakeRelativeUri([Uri] (Resolve-Path $path).Path).ToString()
}

$Out = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $PWD.Path $Out }
New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$pageFolder = (Resolve-Path (Split-Path -Parent $Out)).Path.TrimEnd('\/')
$style = @'
body { font: 14px/1.5 "Segoe UI", sans-serif; margin: 24px; background: #15171a; color: #d8dade; }
h1 { font-size: 20px; } h2 { font-size: 16px; margin-top: 32px; border-bottom: 1px solid #333; }
table { border-collapse: collapse; margin: 8px 0 16px; }
td, th { padding: 2px 12px 2px 0; text-align: left; vertical-align: top; }
th { color: #8b9199; font-weight: normal; }
.FAIL { color: #ff6b6b; } .PASS { color: #6bbf6b; } .WORSE { color: #ff9f43; } .BETTER { color: #4dabf7; }
.number { text-align: right; font-variant-numeric: tabular-nums; }
.note { color: #8b9199; }
figure { display: inline-block; margin: 0 16px 16px 0; }
figcaption { color: #8b9199; }
img { max-width: 620px; border: 1px solid #333; display: block; }
a { color: #4dabf7; }
'@

$failed = @($judged | Where-Object { $_.Pass -eq $false })
$worse = @($judged | Where-Object { (Get-Verdict $_) -eq 'WORSE' })
$html = [Collections.Generic.List[string]]::new()
$html.Add('<!doctype html>')
$html.Add('<html lang="en"><head><meta charset="utf-8"><title>Magic visual tests</title>')
$html.Add("<style>$style</style></head><body>")
$html.Add("<h1>Magic visual tests, the $($written.tier) tier, $((Get-Date).ToString('yyyy-MM-dd HH:mm'))</h1>")
$html.Add("<p>$($failed.Count) of $($judged.Count) rows failed over $(@($written.jobs).Count) view(s) and hour(s), " +
    "$($worse.Count) worse than the baseline.</p>")

foreach ($job in $written.jobs) {
    $html.Add("<h2>$(Get-Escaped $job.name)</h2>")
    $html.Add("<p>The run of Magic took $([string]::Format('{0:0}', $job.seconds)) s. " +
        "Its frames are in $(Get-Escaped $job.folder).</p>")
    $html.Add('<table><tr><th>measurement</th><th class="number">value</th><th>limit</th><th></th><th></th>' +
        '<th>note</th></tr>')
    $its = @($judged | Where-Object { $_.Job -eq $job.name })
    # The rows in the order the page shows them: what failed, then what got worse, then the rest.
    $ordered = @($its | Where-Object { $_.Pass -eq $false }) +
        @($its | Where-Object { $_.Pass -ne $false -and (Get-Verdict $_) -eq 'WORSE' }) +
        @($its | Where-Object { $_.Pass -ne $false -and (Get-Verdict $_) -ne 'WORSE' })
    foreach ($row in $ordered) {
        $pass = if ($null -eq $row.Pass) { '' } elseif ($row.Pass) { 'PASS' } else { 'FAIL' }
        $verdict = Get-Verdict $row
        $html.Add("<tr><td>$(Get-Escaped $row.Name)</td><td class=`"number`">$(Get-Escaped $row.Value)</td>" +
            "<td>$(Get-Escaped $row.Limit)</td><td class=`"$pass`">$pass</td>" +
            "<td class=`"$verdict`">$(Get-Escaped $verdict)</td>" +
            "<td class=`"note`">$(Get-Escaped $row.Note)</td></tr>")
    }
    $html.Add('</table>')

    # Each picture that a measure of this view and hour wrote, with the caption it gave.
    foreach ($picture in @($job.pictures)) {
        $path = Get-Relative $job.folder $picture.file $pageFolder
        if ($path) {
            $html.Add("<figure><a href=`"$path`"><img src=`"$path`" alt=`"$(Get-Escaped $picture.file)`"></a>" +
                "<figcaption>$(Get-Escaped $picture.caption)</figcaption></figure>")
        }
    }
}
$html.Add('</body></html>')
[IO.File]::WriteAllText($Out, ($html -join "`n") + "`n")
Write-Output "report:    $Out"

Write-Output ''
Write-Output "$($failed.Count) of $($judged.Count) rows failed, $($worse.Count) worse than the baseline."
if ($worse.Count -gt 0) { exit 1 }
exit ([int] ($failed.Count -gt 0) * 2)
