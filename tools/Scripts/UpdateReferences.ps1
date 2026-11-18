<#
.SYNOPSIS
Renders the Blender Cycles reference of each view of the cabin scene and stores it in the repository. This is the one
script that runs Cycles.

.DESCRIPTION
A view's camera is an entity of the domain with the tag render-reference, and its hours and its named points are in
ReferenceViews.json beside this script, keyed by the view's name. Magic resolves that entity's pose; this script
gives it to Blender with the job and keeps it in the manifest, so the pose has one source. The reference of a view
and hour is tools/Reference/References/<view>-<hour>.exr, a linear HDR picture, with its mask beside it and a
preview picture under tools/Reference/Previews. The manifest of that folder holds what each reference was rendered from.

Run it by hand after a change to the scene: the materials, the models, the lights, the camera of a view, the hours, or
the code that builds the scene in Blender. With no option it renders what is missing or was rendered from a scene that
moved on, and leaves the rest. The visual tests never render: they read these files, and stop when one is missing or
stale (tools/Scripts/VisualTests.ps1).

The references are large pictures that git keeps with Git LFS. A new clone needs `git lfs install` one time, and the
checkout step of a CI job needs `lfs: true`.

.PARAMETER View
The names of the views to render. All views when not given.

.PARAMETER Hour
The hours to render each view at. Each view's own hours when not given.

.PARAMETER Samples
Cycles' samples a pixel, in place of VisualTests.json's. A reference rendered at another number is stale.

.PARAMETER All
Renders every chosen view and hour again, even the references that are current.

.PARAMETER Blender
The path of blender.exe.

.EXAMPLE
tools/Scripts/UpdateReferences.ps1

.EXAMPLE
tools/Scripts/UpdateReferences.ps1 -View cabin-room,cabin-outside -Hour 12 -Samples 64
#>
[CmdletBinding()]
param(
    [string[]] $View,
    [string[]] $Hour,
    [ValidateRange(1, 100000)]
    [int] $Samples,
    [switch] $All,
    [string] $Blender
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scripts = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scripts 'ReferenceLib.ps1')

# A list given to powershell -File comes in as one string of commas, so both lists are split again here.
$View = @($View | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$asked = @($Hour | ForEach-Object { $_ -split ',' } | Where-Object { $_ } |
    ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })

$settings = Read-Json (Join-Path $scripts 'VisualTests.json')
if (-not $Samples) { $Samples = $settings.cycles.samples }
# The references are rendered at the size that the visual tests' window writes, so that Magic's picture and the
# reference hold the same pixels.
$size = @("$($settings.picture.width)", "$($settings.picture.height)")
$Blender = Get-BlenderPath $Blender

$views = Get-ReferenceViews $View -CheckTags
$states = Get-ReferenceStates $views $asked $Samples $size
$manifest = Read-ReferenceManifest

# What this run renders: the references that are missing or stale, or every one of them with -All.
$jobs = [Collections.Generic.List[object]]::new()
foreach ($each in $views) {
    foreach ($at in Get-ViewHours $each $asked) {
        $paths = Get-ReferencePaths $each.name $at
        $state = $states[$paths.Name]
        if (-not $All -and $state -eq 'current') {
            Write-Output "current    $($paths.Name)"
            continue
        }
        $jobs.Add([pscustomobject]@{
            View  = $each
            Hour  = $at
            State = $state
            Paths = $paths
            Hash  = Get-ReferenceHash $each $at $Samples $size
            # The pose poses Blender's camera and goes into the manifest, so that a test sets Magic's camera to
            # the pose of its own reference.
            Pose  = Get-ViewPose $each.project $each.entity
        })
    }
}
if ($jobs.Count -eq 0) {
    Write-Output 'Every reference of the chosen views is current. Nothing was rendered.'
    exit 0
}

# One build of the scene renders every reference of its project, which is most of the time of a run.
$started = Get-Date
foreach ($project in @($jobs.View.project | Sort-Object -Unique)) {
    $here = @($jobs | Where-Object { $_.View.project -eq $project })
    Write-Output ("Rendering $($here.Count) reference(s) of $project at $($size -join ' x '), $Samples samples, " +
        'from one build of the scene.')
    $list = Join-Path ([IO.Path]::GetTempPath()) 'UpdateReferences-jobs.json'
    # The mask is of the view alone, because it comes from the alpha of the render and every hour of a view masks the
    # same pixels, so the first job of each view writes it and the others read it.
    $masked = @{}
    Write-JsonList $list @($here | ForEach-Object {
        $mask = if ($masked.ContainsKey($_.View.name)) { $null } else { $_.Paths.Mask }
        $masked[$_.View.name] = $true
        # The preview that the docs show is written right after the render of its own job, so no second Blender run
        # opens the EXR again. The pose goes with the job, so that Blender poses the camera from the one pose that
        # the engine resolved, and finds no camera of its own.
        @{ view = $_.View.name; hour = $_.Hour; out = $_.Paths.Picture; mask = $mask
            preview = $_.Paths.Preview
            pose = [ordered]@{ position = $_.Pose.position; rotation = $_.Pose.rotation } }
    })
    New-Item -ItemType Directory -Force (Split-Path -Parent $here[0].Paths.Preview) | Out-Null
    # The picture, the mask and the preview of each job go before the render, so that a render that fails leaves no
    # older file behind for the next run to read as the new one.
    Remove-Item -Force -ErrorAction Ignore @($here | ForEach-Object {
        $_.Paths.Picture; $_.Paths.Mask; $_.Paths.Preview
    })
    Invoke-Blender $Blender 'BuildScene.py' @('--jobs', $list, '--samples', $Samples) 14400 |
        Where-Object { $_ -match '^(built|rendered|rendering on|preview)' } | Write-Output
}

foreach ($job in $jobs) {
    if (-not (Test-Path $job.Paths.Picture)) { throw "Blender wrote no reference of $($job.Paths.Name)." }
    $manifest[$job.Paths.Name] = [ordered]@{
        hash = $job.Hash
        pose = [ordered]@{ position = $job.Pose.position; rotation = $job.Pose.rotation }
    }
}
Write-ReferenceManifest $manifest

Write-Output ''
Write-Output ('{0,-24} {1,-8} {2,10} {3,10} {4,10}' -f 'reference', 'was', 'picture', 'mask', 'preview')
foreach ($job in $jobs) {
    $sizes = @($job.Paths.Picture, $job.Paths.Mask, $job.Paths.Preview | ForEach-Object {
        if (Test-Path $_) { [string]::Format($script:Invariant, '{0:0.0} MB', (Get-Item $_).Length / 1MB) }
        else { '-' }
    })
    Write-Output ('{0,-24} {1,-8} {2,10} {3,10} {4,10}' -f $job.Paths.Name, $job.State, $sizes[0], $sizes[1],
        $sizes[2])
}
Write-Output ''
Write-Output ([string]::Format($script:Invariant,
    '{0} reference(s) rendered in {1:0} s, {2} already current. Commit tools/Reference.',
    $jobs.Count, ((Get-Date) - $started).TotalSeconds, ($states.Count - $jobs.Count)))
