<#
.SYNOPSIS
The parts that UpdateReferences.ps1, VisualTests.ps1 and JudgeVisualTests.ps1 share: the views and their camera poses, a
run of Blender, the stored Cycles reference of a view and an hour, and the readers and writers of the tools' own JSON.

.DESCRIPTION
Dot-source it from a script in this folder:

    . (Join-Path $PSScriptRoot 'ReferenceLib.ps1')

tools/Reference/References holds one reference of each view and hour, with the manifest that says what each one was
rendered from. UpdateReferences.ps1 writes them; every other script reads them.
#>

# The entities that mark a reference view's camera carry this tag.
$script:ViewTag = 'render-reference'

# The project file hashes that Get-ProjectHash has read, which is the slow part of a reference's hash.
$script:ProjectHashes = @{}

# The poses that Get-TaggedPoses has asked Magic for, by project: one headless run of Magic a project a run.
$script:TaggedPoses = @{}

$script:Invariant = [Globalization.CultureInfo]::InvariantCulture

# The repository's root, from this script's folder.
function Get-RepositoryRoot { return Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }

# A number written the way every tool of this folder reads it, whatever the shell's language is.
function Format-Number([double] $Value) { return [string]::Format($script:Invariant, '{0}', $Value) }

# Whether a JSON object read by ConvertFrom-Json holds a field at all. A field that holds the default of its type is
# not in the JSON that Magic writes, so this is how a missing value is told from a value of zero.
function Test-Field($Object, [string] $Name) { return $Object.PSObject.Properties.Name -contains $Name }

# Reads one of the tools' own JSON files, which hold no comments.
function Read-Json([string] $Path) { return Get-Content -Raw $Path | ConvertFrom-Json }

# Writes $Value as JSON with the line endings that git keeps, not the shell's, because these files are committed or
# read again by another run.
function Write-Json([string] $Path, $Value, [int] $Depth = 8) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth $Depth) -replace "`r`n", "`n") + "`n")
}

# Writes $Items as a JSON array. ConvertTo-Json of one object alone is no array in Windows PowerShell, so each object
# is written on its own and the brackets are put around them.
function Write-JsonList([string] $Path, $Items, [int] $Depth = 8) {
    $each = @($Items | ForEach-Object { $_ | ConvertTo-Json -Depth $Depth -Compress })
    [IO.File]::WriteAllText($Path, '[' + ($each -join ',') + "]`n")
}

# The path of blender.exe, which every script of this folder takes as -Blender.
function Get-BlenderPath([string] $Blender) {
    if (-not $Blender) { $Blender = 'C:/Program Files (x86)/Steam/steamapps/common/Blender/blender.exe' }
    if (-not (Test-Path $Blender)) { throw "No Blender at ${Blender}: give its path with -Blender." }
    return $Blender
}

# The folder of the stored references, which is made when it is not there.
function Get-ReferenceFolder {
    $folder = Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'Reference') 'References'
    New-Item -ItemType Directory -Force $folder | Out-Null
    return $folder
}

# Every path of one view and hour: the name they are known by, the stored reference and its mask, the preview picture,
# the baseline of its rows, and with -Under the folder of a run that keeps its frames and its pictures. The mask is of
# the view alone, because it comes from the alpha of the render and every hour of a view masks the same pixels.
function Get-ReferencePaths([string] $View, [double] $At, [string] $Under, [int] $Run = 1) {
    $reference = Join-Path (Split-Path -Parent $PSScriptRoot) 'Reference'
    $name = "$View-$At"
    return @{
        Name     = $name
        Picture  = Join-Path (Get-ReferenceFolder) "$name.exr"
        Mask     = Join-Path (Get-ReferenceFolder) "$View-mask.png"
        Preview  = Join-Path (Join-Path $reference 'Previews') "$name-cycles.jpg"
        Baseline = Join-Path (Join-Path $reference 'Baseline') "$name.json"
        Out      = if ($Under) { Join-Path $Under "$name$(if ($Run -gt 1) { "-run$Run" })" } else { $null }
    }
}

# The limit of a row as the least and the most it may be, with the text that the table prints, or nothing when
# VisualTests.json holds no limit under the key.
function Get-LimitRange($Limits, [string] $Key) {
    if (-not $Key -or -not (Test-Field $Limits $Key)) { return $null }
    $range = $Limits.$Key
    $least = if (Test-Field $range 'min') { [double] $range.min } else { [double]::NegativeInfinity }
    $most = if (Test-Field $range 'max') { [double] $range.max } else { [double]::PositiveInfinity }
    $text = if ($least -eq [double]::NegativeInfinity) { "at most $most" }
        elseif ($most -eq [double]::PositiveInfinity) { "at least $least" }
        else { "$least to $most" }
    return @{ Least = $least; Most = $most; Text = $text }
}

# How far a value lies outside its limit, which is 0 while it passes. A baseline verdict is the change in it. The
# floor is written 0.0, because Windows PowerShell gives [Math]::Max a whole number of two the integer overload, which
# would cut every distance under one to zero.
function Get-Outside([double] $Value, $Range) {
    if ($null -eq $Range) { return 0.0 }
    return [Math]::Max(0.0, [Math]::Max($Range.Least - $Value, $Value - $Range.Most))
}

# The number that a field of a JSON object holds, or $Default where the object leaves the field out.
function Get-Number([object] $Held, [string] $Field, [double] $Default) {
    if ($null -eq $Held -or -not (Test-Field $Held $Field)) { return $Default }
    return [double] $Held.$Field
}

# Where each camera of a project's domain stands and how it is turned, by the name of the entity that marks it: every
# entity that carries the view tag, as the engine itself resolves the world. Magic runs headless one time a project a
# run and prints them with `query domain --tags`, so no script reads a prefab file and no script holds the tag's hash.
function Get-TaggedPoses([string] $Project) {
    if ($script:TaggedPoses.ContainsKey($Project)) { return $script:TaggedPoses[$Project] }
    $out = Join-Path ([IO.Path]::GetTempPath()) "TaggedPoses-$([guid]::NewGuid()).json"
    try {
        & (Join-Path $PSScriptRoot 'Run.ps1') -Headless -Project $Project -Seconds 420 -Commands @(
            'wait loaded', "query domain --tags $script:ViewTag --out $out", 'exit') | Out-Null
        if (-not (Test-Path $out)) {
            throw "Magic printed no entity tagged '$script:ViewTag' of ${Project}: see the run's log above."
        }
        $poses = @{}
        foreach ($entity in (Read-Json $out).entities) {
            if (-not $entity.name) { throw "An entity tagged '$script:ViewTag' in $Project has no name." }
            $transform = $entity.components.Transform
            # A member that holds what a new value of its type holds is not in the JSON: a 0 in a position or a
            # rotation that is there, and a whole rotation that is no turn, which is 0 0 0 1.
            $rotation = if (Test-Field $transform 'rotation') {
                @('x', 'y', 'z', 'w' | ForEach-Object { Get-Number $transform.rotation $_ 0 })
            } else { @(0, 0, 0, 1) }
            $position = if (Test-Field $transform 'position') { $transform.position } else { $null }
            $poses[$entity.name] = @{
                position = @('x', 'y', 'z' | ForEach-Object { Get-Number $position $_ 0 })
                rotation = $rotation
            }
        }
        $script:TaggedPoses[$Project] = $poses
        return $poses
    }
    finally {
        Remove-Item $out -ErrorAction SilentlyContinue
    }
}

# The pose that the stored reference of a view and hour was rendered at, which a test sets Magic's camera to, so that
# a test uses the pose of its own reference and needs no run of Magic to ask for one.
function Get-ReferencePose([string] $View, [double] $At) {
    $name = (Get-ReferencePaths $View $At).Name
    $manifest = Read-ReferenceManifest
    if (-not $manifest.ContainsKey($name) -or -not $manifest[$name].pose) {
        throw ("The manifest of the references holds no pose of $name. Render it again with " +
            'tools/Scripts/UpdateReferences.ps1.')
    }
    $pose = $manifest[$name].pose
    return @{ position = @($pose.position); rotation = @($pose.rotation) }
}

# The transform of the entity that one view names: where the camera stands and how it is turned. This is the one place
# that reads a view's camera by a name: the view's 'entity' field of ReferenceViews.json names it, as ReferenceView
# and the view's name in upper camel case, so 'cabin-wall' names 'ReferenceView.CabinWall'. Nothing else matches a
# name: the render script poses its camera from the pose that this function gives.
function Get-ViewPose([string] $Project, [string] $Entity) {
    $poses = Get-TaggedPoses $Project
    if (-not $poses.ContainsKey($Entity)) {
        throw "No entity '$Entity' tagged '$script:ViewTag' in a globals.prefab of $Project."
    }
    return $poses[$Entity]
}

# The views of ReferenceViews.json, those of -View alone when it is given. With -CheckTags, each view's entity must
# carry the view tag and each tagged entity must have a view: a camera that no view reads, or a view whose camera is
# not tagged, is a fault of the data that would show as a missing reference much later. The check asks Magic for the
# tagged entities, so only UpdateReferences.ps1 makes it; a test run reads the poses of its references instead.
function Get-ReferenceViews([string[]] $View, [switch] $CheckTags) {
    # Windows PowerShell gives the whole JSON list as one value, which an assignment unwraps and @() would not.
    $views = Read-Json (Join-Path $PSScriptRoot 'ReferenceViews.json')
    foreach ($project in @(if ($CheckTags) { $views | ForEach-Object { $_.project } | Sort-Object -Unique })) {
        $poses = Get-TaggedPoses $project
        $named = @($views | Where-Object { $_.project -eq $project } | ForEach-Object { $_.entity })
        foreach ($entity in $named) {
            if (-not $poses.ContainsKey($entity)) {
                throw ("The view of '$entity' has no entity of that name tagged '$script:ViewTag' in the domain of " +
                    "${project}: tag it, or take the view out of ReferenceViews.json.")
            }
        }
        foreach ($entity in $poses.Keys) {
            if ($named -notcontains $entity) {
                throw ("The entity '$entity' of $project is tagged '$script:ViewTag' and no view of " +
                    'ReferenceViews.json names it: give it a view with its hours and its points, or take the tag ' +
                    'off.')
            }
        }
    }
    if (-not $View) { return $views }
    $chosen = @($views | Where-Object { $View -contains $_.name })
    if ($chosen.Count -eq 0) { throw "No view of $($View -join ', ') is in ReferenceViews.json." }
    return $chosen
}

# The hours that a run reads of one view: those of -Hour when the run names any, else the view's own.
function Get-ViewHours([object] $ViewJson, [double[]] $Hours) {
    return @(if ($Hours.Count -gt 0) { $Hours } else { $ViewJson.hours })
}

# Runs Blender with a script of tools/Reference and its arguments, within a time limit, and returns what it prints. A
# script that raises ends the run: the caller never reads a file that the failed script did not write.
function Invoke-Blender([string] $Blender, [string] $Script, [string[]] $Arguments, [int] $Seconds) {
    $path = Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'Reference') $Script
    $out = [IO.Path]::GetTempFileName()
    $failed = [IO.Path]::GetTempFileName()
    # Without --python-exit-code, Blender ends with 0 after a script raises, and only its output says so.
    $quoted = @('-b', '--python-exit-code', '1', '--python', "`"$path`"", '--') +
        @($Arguments | ForEach-Object { "`"$_`"" })
    $process = Start-Process $Blender -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $out `
        -RedirectStandardError $failed
    # Touching the handle keeps the exit code of the process, which Windows otherwise lets go as the process ends.
    $null = $process.Handle
    if (-not $process.WaitForExit($Seconds * 1000)) {
        $process.Kill()
        throw "Blender ran past $Seconds s: $Script $Arguments"
    }
    $text = @(Get-Content $out)
    $errors = @(Get-Content $failed)
    Remove-Item $out, $failed
    if ($process.ExitCode -ne 0) {
        $text | Write-Host
        throw ("Blender failed with exit code $($process.ExitCode): $Script $Arguments`n" +
            (@($errors | Select-Object -Last 20) -join "`n"))
    }
    return $text
}

# What the Cycles reference of a view is built from: the view, the hour, the samples, the size, Cycles' own settings
# and the files of the project, the render script and the add-on. The manifest holds this hash of each reference, so a
# reference of a scene that moved on is seen before anything slow runs.
function Get-ReferenceHash([object] $ViewJson, [double] $At, [int] $WithSamples, [string[]] $Size) {
    $text = [Text.StringBuilder]::new()
    [void] $text.Append(($ViewJson | ConvertTo-Json -Depth 10 -Compress))
    [void] $text.Append("|$At|$WithSamples|$($Size -join 'x')|")
    # Cycles' own settings reach the render, apart from the samples, which $WithSamples already holds. The limits, the
    # tolerances and the waits of VisualTests.json do not reach it, and its grade is put on the preview pictures after
    # the render, so none of them is hashed: a run that only changes a limit keeps every reference it has.
    $cycles = (Read-Json (Join-Path $PSScriptRoot 'VisualTests.json')).cycles
    foreach ($name in @($cycles.PSObject.Properties.Name | Where-Object { $_ -ne 'samples' } | Sort-Object)) {
        [void] $text.Append("$name=$($cycles.$name | ConvertTo-Json -Depth 5 -Compress)|")
    }
    [void] $text.Append((Get-ProjectHash $ViewJson.project))
    $stream = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($text.ToString()))
    return (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.Substring(0, 16)
}

# Every folder whose files the scene is built from: the project's own folder, and the folder of each gem of the
# project's list that is itself a project under samples, such as the shared sample that holds the scripts and the
# assets the cabin uses. A change to one of them moves the picture, so it must mark the references stale.
function Get-ProjectFolders([string] $Project) {
    $root = Get-RepositoryRoot
    $folders = @(Join-Path $root (Split-Path -Parent $Project))
    $samples = @(Get-ChildItem -Directory (Join-Path $root 'samples'))
    foreach ($gem in (Read-Json (Join-Path $root $Project)).gems) {
        $folders += @($samples | Where-Object { Test-Path (Join-Path $_.FullName "$gem.csproj") } |
            ForEach-Object { $_.FullName })
    }
    return @($folders | Sort-Object -Unique)
}

# The files of the look: the folders the scene is built from, the script that renders and the add-on that builds the
# scene. Each one counts by what is in it and by its path under the root, never by when it was written, so that the
# hash of a reference is the same in every clone. Only the files that git knows count: a file that git ignores, such
# as a build's output or a scratch file, must not make a reference stale. One project is read one time a run, because
# it is a few seconds of hashing.
function Get-ProjectHash([string] $Project) {
    if ($script:ProjectHashes.ContainsKey($Project)) { return $script:ProjectHashes[$Project] }
    $root = Get-RepositoryRoot
    # The add-on counts by the modules that build the scene, which is every .py of its package but __init__.py: that
    # one holds the operators, the dialogs and the menu items, which no render reads. An edit of it keeps every
    # reference, and an edit of any other module makes them stale.
    $folders = @(Get-ProjectFolders $Project | ForEach-Object { $_.Substring($root.Length + 1).Replace('\', '/') }) +
        @('tools/Reference/BuildScene.py', 'tools/Blender/*.py', ':(exclude)tools/Blender/*/__init__.py')
    $text = [Text.StringBuilder]::new()
    foreach ($name in @(git -C $root ls-files --cached --others --exclude-standard -- $folders | Sort-Object)) {
        $file = Join-Path $root $name
        if (-not (Test-Path $file)) { continue }
        [void] $text.Append("/$name|$((Get-FileHash $file -Algorithm SHA256).Hash)`n")
    }
    $script:ProjectHashes[$Project] = $text.ToString()
    return $script:ProjectHashes[$Project]
}

# The manifest of the stored references: for each view and hour the hash of what it was rendered from and the pose its
# camera stood at.
function Read-ReferenceManifest {
    $path = Join-Path (Get-ReferenceFolder) 'manifest.json'
    if (-not (Test-Path $path)) { return @{} }
    $manifest = @{}
    foreach ($property in (Read-Json $path).PSObject.Properties) { $manifest[$property.Name] = $property.Value }
    return $manifest
}

function Write-ReferenceManifest([hashtable] $Manifest) {
    $kept = [ordered]@{}
    foreach ($name in @($Manifest.Keys | Sort-Object)) { $kept[$name] = $Manifest[$name] }
    Write-Json (Join-Path (Get-ReferenceFolder) 'manifest.json') $kept 4
}

# Whether the stored reference of a view and hour is there and was rendered from the scene as it is now: 'current',
# 'stale' or 'missing'.
function Get-ReferenceState([object] $ViewJson, [double] $At, [int] $WithSamples, [string[]] $Size,
        [hashtable] $Manifest) {
    $paths = Get-ReferencePaths $ViewJson.name $At
    if (-not (Test-Path $paths.Picture) -or -not (Test-Path $paths.Mask)) { return 'missing' }
    if (-not $Manifest.ContainsKey($paths.Name)) { return 'stale' }
    if ($Manifest[$paths.Name].hash -ne (Get-ReferenceHash $ViewJson $At $WithSamples $Size)) { return 'stale' }
    return 'current'
}

# The state of the stored reference of each view and hour that a run reads, by its name.
function Get-ReferenceStates([object[]] $Views, [double[]] $Hours, [int] $WithSamples, [string[]] $Size) {
    $manifest = Read-ReferenceManifest
    $states = [ordered]@{}
    foreach ($view in $Views) {
        foreach ($at in Get-ViewHours $view $Hours) {
            $states[(Get-ReferencePaths $view.name $at).Name] =
                Get-ReferenceState $view $at $WithSamples $Size $manifest
        }
    }
    return $states
}

# Ends the run before anything slow when a reference is missing, or was rendered from another scene. -AllowStale keeps
# a stale reference, for a person who knows that their change to the scene does not reach the picture.
function Assert-ReferenceStates([object] $States, [switch] $AllowStale) {
    $named = @($States.Keys | Where-Object { $States[$_] -eq 'missing' })
    if ($named.Count -gt 0) {
        throw ("No stored reference of $($named -join ', '). Render it with " +
            'tools/Scripts/UpdateReferences.ps1 and run this again.')
    }
    $named = @($States.Keys | Where-Object { $States[$_] -eq 'stale' })
    if ($named.Count -gt 0 -and -not $AllowStale) {
        throw ("The scene moved on since the reference of $($named -join ', ') was rendered. Render it again with " +
            'tools/Scripts/UpdateReferences.ps1, or compare with the stored one as it is: -Stale.')
    }
}
