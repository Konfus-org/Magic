<#
.SYNOPSIS
What the scripts that open a window of Magic both need.

.DESCRIPTION
Dot-source it from a script in this folder:

    . (Join-Path $PSScriptRoot 'RunLib.ps1')
#>

# The display that the window opens on: -Display held to the displays this machine has. A display that it does not
# have makes the engine warn and open the window on the main one anyway.
function Limit-Display([int] $Display) {
    if ([Environment]::OSVersion.Platform -ne 'Win32NT') { return $Display }
    $displays = @(Get-CimInstance -Namespace 'root/wmi' -ClassName 'WmiMonitorBasicDisplayParams' -ErrorAction Ignore)
    return [Math]::Min($Display, [Math]::Max($displays.Count - 1, 0))
}
