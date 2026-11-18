<#
.SYNOPSIS
Runs every visual test of the cabin against its Blender Cycles references and prints PASS or FAIL for each view, hour
and limit.

.DESCRIPTION
The views, their hours and their points are in ReferenceViews.json beside this script, and the limits are in
VisualTests.json. Each view is rendered at the pose of the entity it names, which the manifest of the references holds
and the Blender reference reads too. For each view and hour:

1. The stored Cycles reference of each view and hour is read from tools/Reference/References. This run renders
   nothing: a reference that is not there, or that was rendered from a scene that has moved on, ends the run before
   Magic starts, and names the views to render again with tools/Scripts/UpdateReferences.ps1.
2. Magic runs one time for each view and hour, through Screenshot.ps1, with the lights set to the hour and its window
   at the size of VisualTests.json, so that a run on any display measures the same picture. The Quick tier takes the
   still frames at the view and three frames at each of the two camera angles beside it. The Full tier takes the
   frames of a turn at each speed in VisualTests.json, of the strafe a view asks for, and of the sun moving to
   another hour as well. tools/Reference/MeasureDenoise.py measures how the light settles after each of those moves
   and how noisy the strip that the move brings into view is.
3. tools/Reference/Compare.py measures Magic's light against the reference, and Magic's own light at the other two
   camera angles.
4. Every measure writes its rows as { name, value, limit }, where the limit is a key of VisualTests.json, and the
   pictures it wrote. This script keeps them all in rows.json beside the run's folders and hands that file to
   tools/Scripts/JudgeVisualTests.ps1, which judges the rows, prints the tables and writes the report page. A changed
   limit can be judged again from the same file, with no run of Magic.

Before Magic runs, Compare.py projects every point of every chosen view at the pose and at the two camera angles
beside it. A point outside the picture, or nearer its edge than VisualTests.json's margin, ends the run in a second
and not after a render.

A point of a view can state the light it may hold at most, as the points of the sealed room do, and its leak is then a
row of its own. A view whose every point states one is measured at its own pose alone, because no row of it reads
another camera angle.

.PARAMETER View
The names of the views in ReferenceViews.json to test. All views when not given.

.PARAMETER Hour
The hours to test each view at. Each view's own hours when not given.

.PARAMETER Tier
Full takes every frame and judges every row. Quick leaves out the turns, the strafe and the light change, which is
about half the time of a run. Full when not given.

.PARAMETER Repeat
Runs each view and hour this many times and prints the spread of each row, which is what the tolerances are set from.
It judges no baseline.

.PARAMETER UpdateBaseline
Writes each row's value to tools/Reference/Baseline, which the runs after it compare with. Run it only once the user
has approved the pictures of the run: the baseline is what "no row gets worse" is measured against.

.PARAMETER Samples
Cycles' samples a pixel, in place of VisualTests.json's. A reference rendered at another number is stale.

.PARAMETER Stale
Compares with a stored reference that the scene has moved on from, which the table marks STALE. For a person who
knows that their change to the scene does not reach the picture.

.PARAMETER Configuration
The build configuration whose Magic runs.

.PARAMETER Blender
The path of blender.exe.

.EXAMPLE
tools/Scripts/VisualTests.ps1 -View cabin-outside -Hour 12 -Tier Quick

.EXAMPLE
tools/Scripts/VisualTests.ps1 -View cabin-wall,cabin-outside -Hour 12,22
#>
[CmdletBinding()]
param(
    [string[]] $View,
    [string[]] $Hour,
    [ValidateSet('Quick', 'Full')]
    [string] $Tier = 'Full',
    [ValidateRange(1, 10)]
    [int] $Repeat = 1,
    [switch] $UpdateBaseline,
    [switch] $Stale,
    [ValidateRange(1, 100000)]
    [int] $Samples,
    [ValidateSet('Debug', 'Optimize', 'Release')]
    [string] $Configuration = 'Debug',
    [string] $Blender
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scripts = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scripts 'ReferenceLib.ps1')
. (Join-Path $scripts 'CaptureLib.ps1')

# A list given to powershell -File comes in as one string of commas, so both lists are split again here.
$View = @($View | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$asked = @($Hour | ForEach-Object { $_ -split ',' } | Where-Object { $_ } |
    ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })

$Blender = Get-BlenderPath $Blender
$views = Get-ReferenceViews $View
$settings = Read-Json (Join-Path $scripts 'VisualTests.json')
# The size of every picture of a run: the window is opened at it, and the references were rendered at it.
$picture = @("$($settings.picture.width)", "$($settings.picture.height)")
if (-not $Samples) { $Samples = $settings.cycles.samples }

# The stored reference of each view and hour that this run reads. Nothing is rendered here: a missing reference, or
# one that was rendered from a scene that has moved on, ends the run here, before the points are checked and before
# Magic starts.
$states = Get-ReferenceStates $views $asked $Samples $picture
Assert-ReferenceStates $states -AllowStale:$Stale

# The pose that each view's own reference was rendered at, from the manifest, so that a test run asks Magic for
# nothing. Every hour of a view is rendered at the same entity, so a difference between its hours is a reference that
# was rendered before the camera moved.
$poses = @{}
foreach ($each in $views) {
    $hoursOf = Get-ViewHours $each $asked
    $pose = Get-ReferencePose $each.name $hoursOf[0]
    foreach ($at in $hoursOf) {
        $other = Get-ReferencePose $each.name $at
        if (($other.position -join ',') -ne ($pose.position -join ',') -or
            ($other.rotation -join ',') -ne ($pose.rotation -join ',')) {
            throw ("The references of $($each.name) at $($hoursOf[0]) and at $at were rendered at different poses. " +
                'Render the view again with tools/Scripts/UpdateReferences.ps1.')
        }
    }
    $poses[$each.name] = $pose
}

# A point that falls outside the picture is a fault of the view's data, and Compare.py ends on it. Projecting every
# point of every chosen view here costs a second and saves a whole render.
$places = @{}
foreach ($each in $views) {
    $places[$each.name] = @(Get-Places $poses[$each.name] $settings.angleDegrees `
        -AtItsPoseAlone:(Test-LeakView $each))
}
$list = Join-Path ([IO.Path]::GetTempPath()) 'VisualTests-points.json'
Write-JsonList $list @($views | ForEach-Object { @{ view = $_.name; poses = $places[$_.name] } })
Invoke-Blender $Blender 'Compare.py' @('--check-points', $list) 300 |
    Where-Object { $_ -match 'points at' } | Write-Output

# The command that holds the day of a project at one hour: the day cycle of each light then keeps the sun at the hour
# and the moon opposite it.
function Get-LightCommands([double] $At) { return @("set Day.Hour $(Format-Number $At)") }

# Magic runs one time for each view, hour and repeat, and what each run leaves is kept for the two steps that follow.
$jobs = [Collections.Generic.List[object]]::new()
for ($pass = 1; $pass -le $Repeat; $pass++) {
    foreach ($each in $views) {
        $pose = $poses[$each.name]
        foreach ($at in Get-ViewHours $each $asked) {
            $name = "$($each.name) at $at"
            Write-Output "== $name$(if ($Repeat -gt 1) { " run $pass" })"

            $changed = if ($Tier -eq 'Full') {
                Get-LightCommands (($at + $settings.lightChangeHours) % 24)
            } else { @() }
            $work = Join-Path ([IO.Path]::GetTempPath()) "VisualTests-$($each.name)-$at-$pass"
            New-Item -ItemType Directory -Force $work | Out-Null
            $capture = Get-RunCommands $each $pose $settings $Tier `
                (Get-LightCommands $at) (Join-Path $work 'stats.json') $changed
            # The last line of the run writes how many warnings and errors Magic counted in its own log.
            $lines = @($capture.Commands) + "query log --out `"$(Join-Path $work 'log.json')`""

            # Screenshot.ps1 runs in a process of its own, so that what the run prints is kept in a file.
            # -AllowErrors: an error in the log is a row of the table, not the end of the run. -Settle 0: the run
            # settles after the commands have put the camera at the view, not before them.
            $quoted = ($lines | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ', '
            $runner = Join-Path $work 'run.ps1'
            Set-Content $runner ("& '$(Join-Path $scripts 'Screenshot.ps1')' -Configuration $Configuration " +
                "-Settle 0 -Seconds 900 -AllowErrors -Size $($picture -join ',') -Project '$($each.project)' " +
                "-Commands @($quoted)")
            $log = Join-Path $work 'run.log'
            $started = Get-Date
            & powershell -NoProfile -ExecutionPolicy Bypass -File $runner *>&1 | Tee-Object -FilePath $log | Out-Null
            $seconds = ((Get-Date) - $started).TotalSeconds
            Write-Output ([string]::Format($script:Invariant, '   the run of Magic took {0:0} s.', $seconds))

            $text = @(Get-Content $log)
            $pfms = @($text | ForEach-Object { $_.Trim() } | Where-Object { $_.EndsWith('.pfm') } |
                Sort-Object { Split-Path -Leaf $_ })
            if ($pfms.Count -ne $capture.Labels.Count) {
                throw "The run of '$name' wrote $($pfms.Count) HDR frames, not $($capture.Labels.Count); see $log."
            }
            # The frames of the run stay where Magic wrote them, and the folder of this view and hour sits beside
            # them with its measurements and its pictures.
            $paths = Get-ReferencePaths $each.name $at (Join-Path (Split-Path -Parent $pfms[0]) 'Reference') $pass
            New-Item -ItemType Directory -Force $paths.Out | Out-Null
            # Magic keeps only its last log files, so the log of this run is kept with its measurements.
            Copy-Item (Get-Content -Raw (Join-Path $work 'log.json') | ConvertFrom-Json).path `
                (Join-Path $paths.Out 'magic.log')
            $frames = @{}
            for ($i = 0; $i -lt $pfms.Count; $i++) {
                $label = $capture.Labels[$i]
                if (-not $frames.ContainsKey($label)) { $frames[$label] = [Collections.Generic.List[string]]::new() }
                $frames[$label].Add($pfms[$i])
            }

            # Magic's picture must be the size the references were rendered at, because the two are compared pixel
            # by pixel.
            $size = (Get-Content $frames['still'][0] -TotalCount 2)[1] -split ' '
            if (($size -join 'x') -ne ($picture -join 'x')) {
                throw ("The run of '$name' wrote a picture of $($size -join ' x '), and the references are of " +
                    "$($picture -join ' x '): set picture in VisualTests.json to the size the window writes, and " +
                    'render the references again with tools/Scripts/UpdateReferences.ps1.')
            }
            $jobs.Add([pscustomobject]@{
                Name = $name
                Run = $pass
                View = $each
                Hour = $at
                Pose = $pose
                Places = $capture.Places
                Moves = $capture.Moves
                Frames = $frames
                Work = $work
                Paths = $paths
                Seconds = $seconds
                Stale = $states[$paths.Name] -eq 'stale'
            })
        }
    }
}

# Magic against its reference, with the frames of the two camera angles beside the view in the same call, then the
# settling and the noise of each move. Each measure writes its rows and the pictures it wrote; this script judges
# none of them.
$rows = [Collections.Generic.List[object]]::new()
$results = [Collections.Generic.List[object]]::new()
foreach ($job in $jobs) {
    $pictures = [Collections.Generic.List[object]]::new()
    $pose = @($job.Pose.position + $job.Pose.rotation | ForEach-Object { Format-Number $_ })
    $metrics = Join-Path $job.Paths.Out 'metrics.json'
    $angles = Join-Path $job.Work 'angles.json'
    Write-JsonList $angles @($job.Places | Where-Object { $_.ContainsKey('side') } | ForEach-Object {
        @{ label = $_.label; pose = @($_.pose); frames = @($job.Frames[$_.side]) }
    })
    # The measurements of a job go before the measure writes them, so that no run reads the rows of an older run.
    Remove-Item -Force -ErrorAction Ignore $metrics
    Invoke-Blender $Blender 'Compare.py' (@('--view', $job.View.name, '--pose') + $pose +
        @('--reference', $job.Paths.Picture, '--mask', $job.Paths.Mask, '--out', $job.Paths.Out,
            '--json', $metrics) + @(if ($job.Places.Count -gt 1) { @('--angles', $angles) }) +
        @('--magic') + @($job.Frames['still'])) 900 |
        Where-Object { $_ -match '^(ratio|split|preview|flip|metrics)' } | Write-Output
    $measured = @(Read-Json $metrics)

    # The Quick tier makes no move, so nothing measures the settling or the noise of one.
    if ($job.Moves.Count -gt 0) {
        $list = Join-Path $job.Work 'moves.json'
        Write-JsonList $list @($job.Moves | ForEach-Object {
            @{ label = $_.label; revealed = $_.revealed
                moving = @(if ($_.moving) { $job.Frames[$_.moving] })
                held = @($job.Frames[$_.held]); still = @($job.Frames[$_.still]) }
        })
        # The frames of the moves take the same look as the comparison's pictures, and AgX is Blender's own curve,
        # so this measure runs under Blender too.
        $denoise = Join-Path $job.Paths.Out 'denoise.json'
        Remove-Item -Force -ErrorAction Ignore $denoise
        Invoke-Blender $Blender 'MeasureDenoise.py' @('--moves', $list, '--out', $job.Paths.Out,
            '--json', $denoise) 1800 | Out-Null
        if (-not (Test-Path $denoise)) { throw "MeasureDenoise.py wrote no measurements for '$($job.Name)'." }
        $measured += @(Read-Json $denoise)
    }

    foreach ($written in $measured) {
        foreach ($row in @($written.rows)) {
            $rows.Add([pscustomobject]@{
                job = $job.Name; run = $job.Run; name = $row.name; limit = $row.limit; value = [double] $row.value
                note = if (Test-Field $row 'note') { $row.note } else { '' }
            })
        }
        if (Test-Field $written 'pictures') { @($written.pictures) | ForEach-Object { $pictures.Add($_) } }
    }

    # The two measures that Magic makes itself, read from the files that `query log` and `query stats gpu` wrote.
    $counted = Get-Content -Raw (Join-Path $job.Work 'log.json') | ConvertFrom-Json
    $rows.Add([pscustomobject]@{ job = $job.Name; run = $job.Run; name = 'warnings and errors in the log'
        limit = 'logWarnings'; value = [double] ($counted.warnings + $counted.errors); note = '' })
    if ($counted.warnings + $counted.errors -gt 0) { Write-Output "    see $(Join-Path $job.Paths.Out 'magic.log')" }

    # The GPU time of a frame, the mean of one second at the view before the first screenshot.
    $stats = (Get-Content -Raw (Join-Path $job.Work 'stats.json') | ConvertFrom-Json).stats
    $frame = @($stats | Where-Object { $_ -like '*gpu.frame.duration: *' })
    if ($frame.Count -gt 0) {
        $rows.Add([pscustomobject]@{ job = $job.Name; run = $job.Run; name = 'GPU time of a frame in ms'
            limit = 'gpuFrameMs'; value = [double] (($frame[0] -split ': ')[1] -split ' ')[0]; note = '' })
    }

    if ($job.Run -eq 1) {
        $results.Add([pscustomobject]@{ name = $job.Name; view = $job.View.name; hour = $job.Hour
            folder = $job.Paths.Out; seconds = $job.Seconds; stale = $job.Stale; pictures = @($pictures) })
    }
    Remove-Item -Recurse -Force $job.Work -ErrorAction SilentlyContinue
}

# Every row of the run in one file, which the judge reads. A person judges the same file again after a change to a
# limit or a tolerance, with no run of Magic: tools/Scripts/JudgeVisualTests.ps1 -Run <folder>.
$runFolder = Split-Path -Parent $jobs[0].Paths.Out
$written = Join-Path $runFolder 'rows.json'
Write-Json $written ([pscustomobject]@{ tier = $Tier; jobs = @($results); rows = @($rows) })
Write-Output "rows:      $written"

& (Join-Path $scripts 'JudgeVisualTests.ps1') -Run $written -UpdateBaseline:$UpdateBaseline
exit $LASTEXITCODE
