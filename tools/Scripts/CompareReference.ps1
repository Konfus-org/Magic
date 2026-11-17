<#
.SYNOPSIS
Measures Magic's light against a Blender Cycles reference of the same view, at each of the view's hours.

.DESCRIPTION
The views, their hours and their points are in ReferenceViews.json beside this script. For each view and hour:

1. Blender prints the sun's and the moon's rotation at the hour from the domain (tools/Reference/BuildScene.py --sun).
2. Magic runs once through Screenshot.ps1: the camera held still at a vertical field of view of 60 degrees and the
   view's pose, the lights' DayCycle removed and their rotation set, then a PNG and -Frames HDR screenshots, its light
   before the exposure.
3. Blender builds the view from the files Magic loads and renders it with Cycles at the HDR screenshots' size.
4. Blender compares them (tools/Reference/Compare.py): the ratio map, the split picture and the table of the points.

Everything is written to Screenshots/Reference/<view>-<hour> beside Magic.

.PARAMETER View
The name of one view in ReferenceViews.json. All views when not given.

.PARAMETER Hour
One hour of the view. All its hours when not given.

.PARAMETER Samples
Cycles' samples a pixel.

.PARAMETER Frames
How many of Magic's HDR frames are averaged, which evens out the jitter of one frame.

.PARAMETER Blender
The path of blender.exe.

.EXAMPLE
tools/Scripts/CompareReference.ps1 -View cabin-wall -Hour 12
#>
[CmdletBinding()]
param(
    [string] $View,
    [Nullable[double]] $Hour,
    [ValidateRange(1, 100000)]
    [int] $Samples = 1024,
    [ValidateRange(1, 64)]
    [int] $Frames = 8,
    [string] $Blender = 'C:/Program Files (x86)/Steam/steamapps/common/Blender/blender.exe'
)

$ErrorActionPreference = 'Stop'
$scripts = Split-Path -Parent $MyInvocation.MyCommand.Path
$reference = Join-Path (Split-Path -Parent $scripts) 'Reference'
$views = Get-Content -Raw (Join-Path $scripts 'ReferenceViews.json') | ConvertFrom-Json
if ($View) {
    $views = @($views | Where-Object { $_.name -eq $View })
    if ($views.Count -eq 0) { throw "No view '$View' in ReferenceViews.json." }
}
if (-not (Test-Path $Blender)) { throw "No Blender at ${Blender}: give its path with -Blender." }

$invariant = [Globalization.CultureInfo]::InvariantCulture

# Runs Blender with a script of tools/Reference and its arguments, within a time limit, and returns what it prints.
function Invoke-Blender([string] $Script, [string[]] $Arguments, [int] $Seconds) {
    $out = [IO.Path]::GetTempFileName()
    $quoted = @('-b', '--python', "`"$(Join-Path $reference $Script)`"", '--') + @($Arguments | ForEach-Object { "`"$_`"" })
    $process = Start-Process $Blender -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $out
    if (-not $process.WaitForExit($Seconds * 1000)) {
        $process.Kill()
        throw "Blender ran past $Seconds s: $Script $Arguments"
    }
    $text = @(Get-Content $out)
    Remove-Item $out
    if ($text | Where-Object { $_ -match 'Traceback' }) { $text | Write-Host; throw "Blender failed: $Script" }
    return $text
}

foreach ($each in $views) {
    $hours = if ($null -ne $Hour) { @($Hour) } elseif ($each.hours) { @($each.hours) } else { @($null) }
    foreach ($at in $hours) {
        $hourArguments = if ($null -ne $at) { @('--hour', [string]::Format($invariant, '{0}', $at)) } else { @() }
        $commands = @(
            'wait 20s',
            'set player.scriptcontainer ''[{"id":100411,"degreesPerSec":0},{"id":100412}]''',
            'set Rendering.FieldOfView 60',
            'wait 30'
        )

        # The lights at the hour, set directly: a DayCycle set anew starts from the rotation its light has then.
        if ($null -ne $at) {
            foreach ($line in Invoke-Blender 'BuildScene.py' (@('--view', $each.name, '--sun') + $hourArguments) 120) {
                if ($line -match '^(sun|moon) (\S+) (\S+) (\S+) (\S+)$') {
                    $commands += "set $($Matches[1]).scriptcontainer '[]'"
                    $commands += [string]::Format($invariant, 'set {0}.transform.rotation ''{{"x":{1},"y":{2},"z":{3},"w":{4}}}''', $Matches[1], $Matches[2], $Matches[3], $Matches[4], $Matches[5])
                }
            }
        }

        # The camera's rotation from its yaw about up, then its pitch about its own right.
        $yaw = $each.yaw * [Math]::PI / 360
        $pitch = $each.pitch * [Math]::PI / 360
        $q = @(
            ([Math]::Cos($yaw) * [Math]::Sin($pitch)),
            ([Math]::Sin($yaw) * [Math]::Cos($pitch)),
            (-[Math]::Sin($yaw) * [Math]::Sin($pitch)),
            ([Math]::Cos($yaw) * [Math]::Cos($pitch))
        )
        $p = $each.position
        $commands += [string]::Format($invariant, 'set player.transform.position ''{{"x":{0},"y":{1},"z":{2}}}''', $p.x, $p.y, $p.z)
        $commands += [string]::Format($invariant, 'set player.transform.rotation ''{{"x":{0},"y":{1},"z":{2},"w":{3}}}''', $q[0], $q[1], $q[2], $q[3])
        $commands += @('wait 15s', 'screenshot', 'wait 1')
        for ($i = 0; $i -lt $Frames; $i++) { $commands += @('screenshot --hdr', 'wait 1') }

        $paths = @(& (Join-Path $scripts 'Screenshot.ps1') -Settle 10 -Seconds 300 -Project $each.project -Commands $commands)
        $pngs = @($paths | Where-Object { "$_".EndsWith('.png') } | Sort-Object { Split-Path -Leaf $_ })
        $pfms = @($paths | Where-Object { "$_".EndsWith('.pfm') })
        if ($pfms.Count -lt $Frames) { throw "The run of view '$($each.name)' wrote $($pfms.Count) HDR frames, not $Frames." }

        $name = if ($null -ne $at) { "$($each.name)-$at" } else { $each.name }
        $out = Join-Path (Split-Path -Parent $pngs[0]) "Reference/$name"
        New-Item -ItemType Directory -Force $out | Out-Null
        $size = (Get-Content $pfms[0] -TotalCount 2)[1] -split ' '

        $exr = Join-Path $out 'cycles.exr'
        Invoke-Blender 'BuildScene.py' (@('--view', $each.name, '--out', $exr, '--samples', $Samples, '--width', $size[0], '--height', $size[1]) + $hourArguments) 3600 | Out-Null
        Write-Output "== $name"
        Write-Output "Magic:     $($pngs[0])"
        Write-Output "Cycles:    $exr"
        Invoke-Blender 'Compare.py' (@('--view', $each.name, '--reference', $exr, '--out', $out, '--magic') + $pfms) 300 |
            Where-Object { $_ -match '^(ratio map|split|point|median)|\d' -and $_ -notmatch '^(Blender|Saved|Read|Fra:)' }
    }
}
