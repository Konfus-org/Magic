<#
.SYNOPSIS
The console lines that one run of the visual tests sends to Magic, from the data of the view, its pose and the tier.

.DESCRIPTION
Dot-source it from a script in this folder:

    . (Join-Path $PSScriptRoot 'CaptureLib.ps1')

Get-RunCommands gives back the lines in the order they are sent, the label of each HDR frame that the run takes, and
the moves of the run as the data that tools/Reference/MeasureDenoise.py reads. It starts nothing: a script can print
what a run would send without Magic.
#>

# A rotation turned by so many degrees about the world's up axis, which is how the camera angles of a view are made.
function Get-Turned([double[]] $Rotation, [double] $Degrees) {
    $half = $Degrees * [Math]::PI / 360
    $sine = [Math]::Sin($half)
    $cosine = [Math]::Cos($half)
    return @(
        ($cosine * $Rotation[0] + $sine * $Rotation[2]),
        ($cosine * $Rotation[1] + $sine * $Rotation[3]),
        ($cosine * $Rotation[2] - $sine * $Rotation[0]),
        ($cosine * $Rotation[3] - $sine * $Rotation[1])
    )
}

# The camera's own right axis, which is where a strafe goes: the first column of the rotation's matrix.
function Get-RightAxis([double[]] $Rotation) {
    $x, $y, $z, $w = $Rotation
    return @((1 - 2 * ($y * $y + $z * $z)), (2 * ($x * $y + $z * $w)), (2 * ($x * $z - $y * $w)))
}

# Where a view is measured: its own pose, and the two angles beside it that the tests measure the same points at. A
# view that is judged by a leak limit alone is measured at its own pose, because no row of it reads another angle.
function Get-Places([hashtable] $Pose, [double] $AngleDegrees, [switch] $AtItsPoseAlone) {
    $places = [Collections.Generic.List[object]]::new()
    $places.Add(@{ label = 'at its own pose'; pose = @($Pose.position + $Pose.rotation) })
    if (-not $AtItsPoseAlone) {
        foreach ($degrees in @(-$AngleDegrees, $AngleDegrees)) {
            $places.Add(@{ label = "turned $degrees degrees"
                side = if ($degrees -lt 0) { 'left' } else { 'right' }
                pose = @($Pose.position + (Get-Turned $Pose.rotation $degrees)) })
        }
    }
    return $places
}

# Whether the rows of a view are the leak at each of its points alone: every point states the light it may hold at
# most, as the sealed room's do. Such a view has no ratio, colour or three-angle row, so the run takes no frame at the
# camera angles beside it.
function Test-LeakView([object] $ViewJson) {
    return @($ViewJson.points | Where-Object { Test-Field $_ 'maxNits' }).Count -eq @($ViewJson.points).Count
}

function Get-PositionCommand([double[]] $Position) {
    return [string]::Format($script:Invariant, 'set player.transform.position ''{{"x":{0},"y":{1},"z":{2}}}''',
        $Position[0], $Position[1], $Position[2])
}

function Get-RotationCommand([double[]] $Rotation) {
    return [string]::Format($script:Invariant,
        'set player.transform.rotation ''{{"x":{0},"y":{1},"z":{2},"w":{3}}}''',
        $Rotation[0], $Rotation[1], $Rotation[2], $Rotation[3])
}

# The command that turns and moves the camera, with the Spin and the Glide script of the shared sample. Both step by
# the frame's own time, so the speeds are of real time whatever the frame rate is. The player's own controller is kept,
# because the command sets the whole list.
function Get-MoveCommand([object] $Scene, [double] $DegreesPerSecond, [double] $MetresPerSecond) {
    return [string]::Format($script:Invariant,
        'set player.scriptcontainer ''[{{"id":{0},"degreesPerSec":{1}}},{{"id":{2},"metresPerSec":{3}}},' +
        '{{"id":{4}}}]''', $Scene.spinScript, $DegreesPerSecond, $Scene.glideScript, $MetresPerSecond,
        $Scene.controllerScript)
}

# The commands that hold the cabin's own moving props where its Cycles reference has them: the turning ornament at the
# angle it starts at, and the fire and the lantern at the strength the prefab gives them, because their Flicker script
# moves it every frame. Every frame that a row reads holds them there, so the two pictures show the same scene.
function Get-HeldSceneCommands([object] $Scene) {
    return @(
        'set ornament.scriptcontainer ''[]''',
        'set ornament.transform.rotation ''{"x":0,"y":0,"z":0,"w":1}''',
        'set fire.scriptcontainer ''[]''',
        "set fire.pointlight.intensity $(Format-Number $Scene.fireIntensity)",
        'set lantern.scriptcontainer ''[]''',
        "set lantern.pointlight.intensity $(Format-Number $Scene.lanternIntensity)")
}

# The commands that let the same props move again, for the frames taken in motion: the eye reads those frames and no
# row does, so they show the scene as a run of Magic shows it.
function Get-MovingSceneCommands([object] $Scene) {
    return @(
        [string]::Format($script:Invariant, 'set ornament.scriptcontainer ''[{{"id":{0},"degreesPerSec":{1}}}]''',
            $Scene.spinScript, $Scene.ornamentDegreesPerSecond),
        [string]::Format($script:Invariant, 'set fire.scriptcontainer ''[{{"id":{0},"depth":{1}}}]''',
            $Scene.flickerScript, $Scene.fireFlickerDepth),
        [string]::Format($script:Invariant, 'set lantern.scriptcontainer ''[{{"id":{0},"depth":{1}}}]''',
            $Scene.flickerScript, $Scene.lanternFlickerDepth))
}

<#
.SYNOPSIS
The console lines of one run of Magic, the label of each HDR frame it takes, and the moves it makes.

.DESCRIPTION
It gives back a hashtable of Commands, Labels and Moves. Labels holds one label for each HDR frame, in the order the
run takes them, which is how a frame on disk is known again. Each move of Moves is a label of a group of frames that
MeasureDenoise.py measures together: the frames taken while the move ran, the frames held still after it, and which
edge of the picture the move brought into view.

.PARAMETER Lights
The commands that set the sun and the moon to the hour of the run.

.PARAMETER Stats
The file that the run writes its GPU stats to, measured over one second at the view, once the picture has settled.

.PARAMETER ChangedLights
The commands that move them to another hour, which the Full tier measures the settling of. Without them the run makes
no light change.
#>
function Get-RunCommands([object] $ViewJson, [hashtable] $Pose, [object] $Settings, [string] $Tier,
        [string[]] $Lights, [string] $Stats, [string[]] $ChangedLights) {
    $scene = $Settings.scene
    $frames = $Settings.frames
    $waits = $Settings.waitFrames
    $commands = [Collections.Generic.List[string]]::new()
    $labels = [Collections.Generic.List[string]]::new()
    $moves = [Collections.Generic.List[object]]::new()

    # Each HDR frame, with the label that names what it holds. A frame takes a frame's time to write, so one frame of
    # waiting follows each.
    function Add-Frames([int] $Count, [string] $Label) {
        for ($i = 0; $i -lt $Count; $i++) {
            $commands.Add('screenshot --hdr')
            $commands.Add('wait 1')
            $labels.Add($Label)
        }
    }

    # One move of the camera: $Start puts it away from the view by what the move covers, $Move is the script that
    # moves it to the view's pose over $Seconds, and $Back sets the pose again when the script stops, because the move
    # runs a frame or two past its time. The frames taken in motion are for the eye; the frames held after it are
    # where the renderer's noise shows.
    function Add-Move([string] $Label, [string] $Start, [string] $Move, [double] $Seconds, [string] $Back,
            [string] $Revealed) {
        $commands.Add($Start)
        $commands.Add("wait $($waits.beforeMove)")
        $commands.Add($Move)
        $commands.Add("wait $(Format-Number $Seconds)s")
        Get-MovingSceneCommands $scene | ForEach-Object { $commands.Add($_) }
        Add-Frames $frames.moving "$Label-moving"
        $commands.Add((Get-MoveCommand $scene 0 0))
        $commands.Add($Back)
        $commands.Add('wait 2')
        Get-HeldSceneCommands $scene | ForEach-Object { $commands.Add($_) }
        Add-Frames $frames.moveHeld "$Label-held"
        $moves.Add(@{ label = $Label; revealed = $Revealed; moving = "$Label-moving"; held = "$Label-held"
            still = 'still' })
    }

    # The player's own Spin turns the camera from the first frame, so it is stopped and the camera put at the view
    # before any wait. The lights of the hour and the held props follow at once, so that the one settle wait after
    # them settles the picture that the rows read.
    $commands.Add((Get-MoveCommand $scene 0 0))
    $commands.Add((Get-PositionCommand $Pose.position))
    $commands.Add((Get-RotationCommand $Pose.rotation))
    $fieldOfView = if (Test-Field $ViewJson 'fieldOfView') { [double] $ViewJson.fieldOfView }
        else { [double] $Settings.fieldOfView }
    $commands.Add("set player.camera.fov $(Format-Number $fieldOfView)")
    $Lights | ForEach-Object { $commands.Add($_) }
    Get-HeldSceneCommands $scene | ForEach-Object { $commands.Add($_) }
    $commands.Add("wait $($waits.settle)")
    $commands.Add("query stats gpu --out `"$Stats`"")

    Add-Frames $frames.still 'still'

    # The two camera angles beside the view, which the three-angle row of each point reads.
    $places = @(Get-Places $Pose $Settings.angleDegrees -AtItsPoseAlone:(Test-LeakView $ViewJson))
    foreach ($place in @($places | Where-Object { $_.ContainsKey('side') })) {
        $commands.Add((Get-RotationCommand @($place.pose[3..6])))
        $commands.Add("wait $($waits.angle)")
        Add-Frames $frames.angle $place.side
    }

    if ($Tier -eq 'Full') {
        # The camera goes back to the view before the moves, which each start away from it.
        if ($places.Count -gt 1) {
            $commands.Add((Get-RotationCommand $Pose.rotation))
            $commands.Add("wait $($waits.angle)")
        }
        # Every turn goes in the direction that Get-Turned's positive degrees give, so the right edge of the picture
        # is the one that each of them brings into view.
        foreach ($speed in $Settings.turnSpeedsPerSecond) {
            Add-Move "turn$speed" `
                (Get-RotationCommand (Get-Turned $Pose.rotation (-$speed * $Settings.turnSeconds))) `
                (Get-MoveCommand $scene $speed 0) $Settings.turnSeconds `
                (Get-RotationCommand $Pose.rotation) 'right'
        }
        # The strafe of a view that asks for one: the camera starts the distance to its own left and glides to the
        # view's pose, which gives the parallax that a turn alone never shows. It uncovers what nearer things hid all
        # over the picture, so no one edge is the one it reveals.
        if (Test-Field $ViewJson 'strafe') {
            $metres = [double] $ViewJson.strafe.meters
            $over = [double] $ViewJson.strafe.seconds
            $right = Get-RightAxis $Pose.rotation
            $start = @(0, 1, 2 | ForEach-Object { $Pose.position[$_] - $right[$_] * $metres })
            Add-Move 'strafe' (Get-PositionCommand $start) (Get-MoveCommand $scene 0 ($metres / $over)) $over `
                (Get-PositionCommand $Pose.position) $null
        }
        if ($ChangedLights) {
            $ChangedLights | ForEach-Object { $commands.Add($_) }
            Add-Frames $frames.light 'light'
            $commands.Add("wait $($waits.light)")
            Add-Frames $frames.still 'lightstill'
            $moves.Add(@{ label = 'light change'; revealed = $null; moving = $null; held = 'light'
                still = 'lightstill' })
        }
    }

    return @{ Commands = @($commands); Labels = @($labels); Moves = @($moves); Places = $places }
}
