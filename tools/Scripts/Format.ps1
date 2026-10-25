<#
.SYNOPSIS
Formats the C# a change touches by the repository's .editorconfig: orders members, applies the code style, wraps long
lines, then fixes the whitespace.

.DESCRIPTION
The files are the .cs files that differ from HEAD, staged, unstaged or new, so the change being made is formatted and
no other code is touched; -All formats every file, for when a rule is added. dotnet format makes four passes over them:
the member-order analyzers' fixes, which put a type's members in order; the code-style fixes, except the few that need
a person's judgment, which the build still reports; the line-length and list analyzers' fixes, which wrap a line of
code that is too long and put a wrapped list's items on lines of their own; and whitespace, line endings and the final
newline. The rules are .editorconfig's, the ones the build checks, so what it leaves passes it unless a fix needed
judgment. It reads the code as the last build left it, generated bindings included, and builds nothing.
tools/Scripts/AgentTaskComplete.ps1 runs it first, so an agent's change is formatted before it is validated. Runs from any
current directory.

.PARAMETER All
Formats every C# file in the solution rather than the changed ones.

.EXAMPLE
tools/Scripts/Format.ps1

.EXAMPLE
tools/Scripts/Format.ps1 -All
#>
[CmdletBinding()]
param(
    [switch] $All
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false
# The restore's worker processes end with it instead of lingering, one per core, for a quarter of an hour.
$env:MSBUILDDISABLENODEREUSE = '1'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$solution = Join-Path $root 'Magic.slnx'

# Each pass names the files it formats, and what it formatted.
$common = @('--verbosity', 'normal')
if (-not $All) {
    $changed = @(git -C $root -c core.quotepath=off diff --name-only --diff-filter=d HEAD)
    $new = @(git -C $root -c core.quotepath=off ls-files --others --exclude-standard)
    $files = @(($changed + $new) | Where-Object { $_ -like '*.cs' } | Sort-Object -Unique)
    if ($files.Count -eq 0) {
        Write-Output 'No C# file has changed: nothing to format.'
        exit 0
    }
    $common = @('--include') + $files + $common
}

# The member-order, line-length and list rules .editorconfig turns on are the only analyzer fixes that run: the others
# change what code does. Each kind is a pass of its own, in this order: members move before anything is measured; a
# style fix (a one-line body made =>) can make a line too long; and the wrap measures lines as the passes before it
# left them.
$editorConfig = Join-Path $root '.editorconfig'
function Get-Rules([string] $prefix) {
    @(Select-String -Path $editorConfig -Pattern "^dotnet_diagnostic\.($prefix\d+)\.severity = warning" |
        ForEach-Object { $_.Matches[0].Groups[1].Value })
}
$orderRules = Get-Rules 'SA1'
$wrapRules = Get-Rules 'RCS0'
# Style fixes that delete code, invent it or rename it across the solution, which a person decides: missing switch
# cases, unreachable code, unused members, values and parameters, and names.
$judged = 'IDE0010', 'IDE0072', 'IDE0035', 'IDE0051', 'IDE0052', 'IDE0059', 'IDE0060', 'IDE1006'

$passes = @(
    @{ Name = 'Order'; Run = { dotnet format analyzers $solution --diagnostics $orderRules @common } }
    @{ Name = 'Style'; Run = { dotnet format style $solution --no-restore --exclude-diagnostics $judged @common } }
    @{ Name = 'Wrap'; Run = { dotnet format analyzers $solution --no-restore --diagnostics $wrapRules @common } }
    @{ Name = 'Whitespace'; Run = { dotnet format whitespace $solution --no-restore @common } }
)

# The included paths are relative to the repository, as git gave them.
Push-Location $root
try {
    foreach ($pass in $passes) {
        Write-Output "==> $($pass.Name)"
        & $pass.Run
        if ($LASTEXITCODE -ne 0) {
            Write-Output "FAILED: $($pass.Name) exited $LASTEXITCODE"
            exit $LASTEXITCODE
        }
    }
} finally {
    Pop-Location
}
