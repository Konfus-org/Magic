<#
.SYNOPSIS
What the scripts that run Magic share.

.DESCRIPTION
Dot-source it from a script in this folder:

    . (Join-Path $PSScriptRoot 'RunLib.ps1')
#>

# The Magic that a configuration's build made: the program under Build/bin, with the .exe that Windows builds when it
# is there. This is the one place that names it. Nothing here builds it, so a script that runs it checks that it is
# there (Test-Path).
function Get-MagicPath([string] $Configuration = 'Debug') {
    $root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $magic = Join-Path (Join-Path (Join-Path (Join-Path $root 'Build') 'bin') $Configuration) 'Magic'
    if (Test-Path "$magic.exe") { return "$magic.exe" }
    return $magic
}

# The .magic file of a project: -Project as given, or the one .magic file in it when it is a folder. Relative paths
# start at the repository root.
function Resolve-ProjectFile([string] $Project, [string] $Root) {
    $path = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $Root $Project }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { return $path }
    $files = @(Get-ChildItem -LiteralPath $path -Filter '*.magic' -File)
    if ($files.Count -ne 1) { throw "The folder $path must hold one .magic file, and it holds $($files.Count)." }
    return $files[0].FullName
}

# The name that a window shows in its title bar, so a person can tell whose run it is: the branch checked out in the
# repository, or, on a detached HEAD, the worktree's folder name and the short commit, as "name @ ae111ef6".
function Get-RunName([string] $Root) {
    $branch = git -C $Root branch --show-current
    if ($branch) { return $branch }
    $commit = git -C $Root rev-parse --short=8 HEAD
    return "$(Split-Path -Leaf $Root) @ $commit"
}

# Stops the run when the window's name is empty: a window with no name does not show whose run it is, so Magic does
# not open.
function Assert-RunName([string] $Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) {
        throw 'The window has no name, so the run stops before Magic opens. Give -Name, or run in a git checkout.'
    }
}

# The display that the window opens on: -Display held to the displays this machine has. A display that it does not
# have makes the engine warn and open the window on the main one anyway.
function Limit-Display([int] $Display) {
    if ([Environment]::OSVersion.Platform -ne 'Win32NT') { return $Display }
    $displays = @(Get-CimInstance -Namespace 'root/wmi' -ClassName 'WmiMonitorBasicDisplayParams' -ErrorAction Ignore)
    return [Math]::Min($Display, [Math]::Max($displays.Count - 1, 0))
}
