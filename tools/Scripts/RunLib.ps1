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

# The display that the window opens on: -Display held to the displays this machine has. A display that it does not
# have makes the engine warn and open the window on the main one anyway.
function Limit-Display([int] $Display) {
    if ([Environment]::OSVersion.Platform -ne 'Win32NT') { return $Display }
    $displays = @(Get-CimInstance -Namespace 'root/wmi' -ClassName 'WmiMonitorBasicDisplayParams' -ErrorAction Ignore)
    return [Math]::Min($Display, [Math]::Max($displays.Count - 1, 0))
}
