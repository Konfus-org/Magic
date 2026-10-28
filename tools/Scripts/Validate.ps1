<#
.SYNOPSIS
Runs the checks a change must pass: a clean build, the tests, the samples headless through every one of their domains,
and the samples with a window through every domain, ending in a screenshot of each to look at.

.DESCRIPTION
The steps run in order and the first failure ends the run with that step's exit code; a run that passes exits 0.
The build is incremental, so what is built already is reused, and the native libraries are built once per machine,
shared by every checkout (src/Bundled/Gems/README.md, Native libraries). The build makes every warning but a TODO's an
error, so a clean build is one that exits 0. It logs with MSBuild's plain console logger, so the native libraries'
progress shows as it happens, which the terminal logger holds back until each project ends. Formatting is not checked
here: tools/Scripts/Format.ps1 formats a change before it is validated, and the build fails on every rule it applies that a
build can see.
The tests run against that build, so running them alone (-Only Test) needs one. Both sample runs pass --fail-on-error,
so an error logged during a run exits 2, and both wait until the world has loaded (the console's wait loaded), shaders
compiled and the domain open, so a world that never loads fails rather than passing on the loading screen. Their
ceiling is a time, not a frame count: a headless loading screen costs next to nothing to draw, so thousands of frames
can go by while shaders compile. Each run then opens every other domain under the project's Assets, all but its
loading domain, in place of the world (the console's open), one after another, so a change that breaks any of them
fails. The Screenshot step runs tools/Scripts/Screenshot.ps1 over the same domains and prints the images' paths last, the
first domain's first: open each and look at what it shows before reporting the change as working. It needs a display
and a GPU; the others do not. Runs from any current directory.

.PARAMETER Configuration
The build configuration: Debug, Optimize or Release.

.PARAMETER Seconds
The longest each sample run may last for each domain it opens, in seconds, before it counts as never having loaded: a
ceiling, not a wait.

.PARAMETER Project
The project the sample runs open: a .magic file or the folder holding one, relative to the repository root.

.PARAMETER Only
The steps to run when not all of them: Build, Test, Headless, Screenshot, in that order whatever order they are given.

.EXAMPLE
tools/Scripts/Validate.ps1

.EXAMPLE
tools/Scripts/Validate.ps1 -Only Build,Headless
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Optimize', 'Release')]
    [string] $Configuration = 'Debug',
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Seconds = 600,
    [string] $Project = 'samples',
    [ValidateSet('Build', 'Test', 'Headless', 'Screenshot')]
    [string[]] $Only = @('Build', 'Test', 'Headless', 'Screenshot')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false
# The build's worker processes end with the build instead of lingering, one per core, for a quarter of an hour.
$env:MSBUILDDISABLENODEREUSE = '1'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$solution = Join-Path (Join-Path $root 'src') 'Magic.slnx'
$magic = Join-Path (Join-Path (Join-Path (Join-Path $root 'Build') 'bin') $Configuration) 'Magic'
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }
$screenshots = @()

# The asset paths of the project's domains but its first domain, which opens first, and its loading domain, which shows
# while another opens: every .domain under its Assets, named by the id its .meta gives, as the .magic file names them.
function Get-Domains {
    $file = if ($projectPath -like '*.magic') { $projectPath } else { @(Get-ChildItem $projectPath -Filter '*.magic')[0].FullName }
    $text = Get-Content -Raw $file
    $skipped = @()
    foreach ($key in 'firstDomain', 'loadingDomain') {
        if ($text -match ('"' + $key + '"\s*:\s*\{\s*"id"\s*:\s*(\d+)')) { $skipped += $Matches[1] }
    }
    $assets = Join-Path (Split-Path -Parent $file) 'Assets'
    $domains = foreach ($domain in Get-ChildItem $assets -Recurse -Filter '*.domain') {
        $meta = Get-Content -Raw "$($domain.FullName).meta"
        if ($meta -match '"id"\s*:\s*(\d+)' -and $skipped -contains $Matches[1]) { continue }
        $domain.FullName.Substring($assets.Length + 1).Replace('\', '/')
    }
    return @($domains | Sort-Object)
}

# Runs the project headless until its world has loaded, opens each other domain in its place until it is there, then
# quits; a run that reaches its -Seconds first never loaded.
function Invoke-Headless {
    $domains = Get-Domains
    $lines = @('wait loaded') + @($domains | ForEach-Object { "open $_" }) + @('quit')
    $log = $lines | & $magic --project $projectPath --seconds ($Seconds * (1 + $domains.Count)) --fail-on-error --headless | ForEach-Object { Write-Host $_; $_ }
    $code = $LASTEXITCODE
    if ($code -eq 0 -and -not ($log | Select-String -SimpleMatch 'quit typed in the terminal')) {
        Write-Host "FAILED: the world never finished loading within $Seconds s."
        $code = 1
    }
    $global:LASTEXITCODE = $code
}

# Runs tools/Scripts/Screenshot.ps1 over every domain and keeps the images' paths, its output.
function Invoke-Screenshot {
    $output = & (Join-Path $PSScriptRoot 'Screenshot.ps1') -Configuration $Configuration -Seconds $Seconds -Project $Project -Domains (Get-Domains)
    $code = $LASTEXITCODE
    if ($code -eq 0) { $script:screenshots = @($output) }
    $global:LASTEXITCODE = $code
}

$steps = @(
    @{ Name = 'Build'; Run = { dotnet build $solution --configuration $Configuration -tl:off -p:CSharpier_Check=true } }
    @{ Name = 'Test'; Run = { dotnet test $solution --configuration $Configuration --no-build } }
    @{ Name = 'Headless'; Run = { Invoke-Headless } }
    @{ Name = 'Screenshot'; Run = { Invoke-Screenshot } }
)

$code = 0
$failed = $null
$summary = @()
foreach ($step in $steps) {
    if ($Only -notcontains $step.Name) { continue }
    if ($step.Name -in 'Headless', 'Screenshot') {
        # Named here rather than at the start: on Windows it is Magic.exe, which the Build step may have just made.
        if (Test-Path "$magic.exe") { $magic = "$magic.exe" }
        if (-not (Test-Path $magic)) {
            Write-Output "There is no $magic to run: build first (tools/Scripts/Validate.ps1 -Only Build)."
            exit 1
        }
    }

    Write-Output ""
    Write-Output "==> $($step.Name)"
    $started = [System.Diagnostics.Stopwatch]::StartNew()
    & $step.Run
    $code = $LASTEXITCODE
    $summary += '{0,-10} exit {1} in {2:n0} s' -f $step.Name, $code, $started.Elapsed.TotalSeconds
    if ($code -ne 0) {
        $failed = $step.Name
        break
    }
}

Write-Output ""
foreach ($line in $summary) { Write-Output $line }
if ($null -eq $failed) {
    Write-Output 'PASSED'
} else {
    Write-Output "FAILED: $failed exited $code"
}
if ($screenshots.Count -gt 0) {
    Write-Output ""
    foreach ($path in $screenshots) { Write-Output "Screenshot: $path" }
    Write-Output 'Open each and look at what it shows before reporting the change as working.'
}
exit $code
