<#
.SYNOPSIS
Flies the camera in through the cabin's open door, turns it to each wall, and flies it out again, once for each time
of day, and prints the screenshots' paths.

.DESCRIPTION
One run of samples/Terrain through tools/Scripts/Run.ps1, which opens the Cabin domain. The run holds the player
still, then, for each hour of -Hours, holds the sun at that hour and sends the camera along the way: outside the
door, a step nearer, the doorway, the middle of the room at four headings 90 degrees apart, the floor under the
window, and back out through the door. The steps between the stops show what a move does to the picture.

At each stop the run waits -Settle seconds for the exposure and the light to settle, takes a screenshot, and waits
-Write seconds for it to be written. The output is the paths of the screenshots, in the order they were taken, so a
caller can keep them: $png = samples/Terrain/FlyThrough.ps1. Open each one and look at it: a screenshot that was
taken is not one that shows the lighting working.

Needs a build (tools/Scripts/Validate.ps1 -Only Build), a display and a GPU. Runs from any current directory.

.PARAMETER Configuration
The build configuration whose Magic runs: Debug, Optimize or Release.

.PARAMETER Hours
The times of day to fly the way at, from 0 to 24, where 12 is noon.

.PARAMETER Settle
How many seconds pass at each stop before its screenshot, for the exposure and the light to settle.

.PARAMETER Write
How many seconds pass after each screenshot, for the frame to be read back and written.

.PARAMETER Seconds
The longest the run may last, in seconds. A run still going then is killed.

.EXAMPLE
samples/Terrain/FlyThrough.ps1

.EXAMPLE
samples/Terrain/FlyThrough.ps1 -Hours 12
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Optimize', 'Release')]
    [string] $Configuration = 'Debug',
    [double[]] $Hours = @(12, 18.5, 1, 6.5),
    [ValidateRange(0, [int]::MaxValue)]
    [int] $Settle = 4,
    [ValidateRange(0, [int]::MaxValue)]
    [int] $Write = 4,
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Seconds = 1800
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The way through the cabin, in the order it is flown. Each stop gives the camera a place and a heading, both whole in
# JSON, because a console value whose first word starts with '-' is read as an option. Each is sent in single quotes,
# which is what keeps the double quotes of the JSON, so the lines below wrap it. The headings are quaternions about
# the world's up, a quarter turn apart, each named for the wall it faces.
$intoCabin = '{"x":0,"y":-0.5373,"z":0,"w":0.84339}'
$hearth = '{"x":0,"y":0.21644,"z":0,"w":0.9763}'
$towardDoor = '{"x":0,"y":0.84339,"z":0,"w":0.5373}'
$fourthWall = '{"x":0,"y":0.9763,"z":0,"w":-0.21644}'
# The heading toward the door, pitched 30 degrees down, so the floor under the window fills the picture.
$underWindow = '{"x":0.13907,"y":0.81465,"z":-0.21829,"w":0.51899}'

$outside = '{"x":-2891.282,"y":63.19,"z":-1819.902}'
$approach = '{"x":-2893.21,"y":63.49,"z":-1819.003}'
$doorway = '{"x":-2895.138,"y":63.79,"z":-1818.104}'
$inside = '{"x":-2896.518,"y":63.79,"z":-1817.461}'
$middle = '{"x":-2897.898,"y":63.79,"z":-1816.817}'

$way = @(
    @{ Name = 'outside the door'; Position = $outside; Rotation = $intoCabin },
    @{ Name = 'a step nearer'; Position = $approach; Rotation = $intoCabin },
    @{ Name = 'the doorway'; Position = $doorway; Rotation = $intoCabin },
    @{ Name = 'a step inside'; Position = $inside; Rotation = $intoCabin },
    @{ Name = 'the middle, facing the back wall'; Position = $middle; Rotation = $intoCabin },
    @{ Name = 'the middle, facing the hearth'; Position = $middle; Rotation = $hearth },
    @{ Name = 'the middle, facing the door wall'; Position = $middle; Rotation = $towardDoor },
    @{ Name = 'the middle, facing the fourth wall'; Position = $middle; Rotation = $fourthWall },
    @{ Name = 'the floor under the window'; Position = $middle; Rotation = $underWindow },
    @{ Name = 'a step toward the door'; Position = $inside; Rotation = $towardDoor },
    @{ Name = 'the doorway, facing out'; Position = $doorway; Rotation = $towardDoor },
    @{ Name = 'a step out'; Position = $approach; Rotation = $towardDoor },
    @{ Name = 'outside again'; Position = $outside; Rotation = $towardDoor }
)

# The world is loaded and settled, the spin is held at no turn so the camera keeps the heading each stop gives it, and
# the camera's own script is kept, because the whole component is written. The sun's script keeps the copy that runs,
# so writing the component only hands it the hour to hold.
$lines = @('wait loaded', 'wait drawn', 'wait 20s', "set player.scriptcontainer '[{""id"":100411,""degreesPerSec"":0},{""id"":100412}]'", 'wait 30')
foreach ($hour in $Hours) {
    $text = $hour.ToString([cultureinfo]::InvariantCulture)
    $lines += @("set sun.scriptcontainer '[{""id"":100421,""hour"":$text,""hoursPerMinute"":0}]'", "wait ${Settle}s")
    foreach ($stop in $way) {
        $lines += @(
            "set player.transform.position '$($stop.Position)'",
            "set player.transform.rotation '$($stop.Rotation)'",
            "wait ${Settle}s",
            'screenshot',
            "wait ${Write}s"
        )
    }
}
$lines += 'wait 10s'

$runner = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools/Scripts/Run.ps1'
$output = & $runner -Configuration $Configuration -Project 'samples/Terrain/Terrain.magic' -Seconds $Seconds -Commands ($lines + 'exit')
$code = $LASTEXITCODE
$shots = @($output | Where-Object { $_ -match '\.png$' })

$taken = 0
foreach ($hour in $Hours) {
    foreach ($stop in $way) {
        if ($taken -lt $shots.Count) { Write-Host "hour $hour, $($stop.Name): $($shots[$taken])" }
        $taken++
    }
}
$shots | ForEach-Object { Write-Output $_ }

if ($shots.Count -ne $Hours.Count * $way.Count) {
    Write-Host "FAILED: $($shots.Count) screenshots of the $($Hours.Count * $way.Count) the way asks for."
    exit 1
}
exit $code
