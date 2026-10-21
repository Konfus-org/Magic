<#
.SYNOPSIS
Formats the C# a change touches by the repository's .editorconfig: orders members and wraps long lines, then applies
the code-style rules and the whitespace ones.

.DESCRIPTION
The files are the .cs files that differ from HEAD, staged, unstaged or new, so the change being made is formatted and
no other code is touched. dotnet format makes four passes over them: the member-order analyzers' fixes, which put a
type's members in order; the line-length analyzer's fix, which wraps a line of code that is too long, once its member
has moved; the code-style fixes, except the few that need a person's judgment, which the build still reports; and
whitespace, line endings and the final newline. The rules are
.editorconfig's, the ones the build and Validate's Format step check, so what it leaves passes them unless a fix needed
judgment. It reads the code as the last build left it, generated bindings included, and builds nothing.
tools/AgentTaskComplete.ps1 runs it first, so an agent's change is formatted before it is validated. Runs from any
current directory.

.EXAMPLE
tools/Format.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false
# The restore's worker processes end with it instead of lingering, one per core, for a quarter of an hour.
$env:MSBUILDDISABLENODEREUSE = '1'

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'Magic.slnx'

$changed = @(git -C $root -c core.quotepath=off diff --name-only --diff-filter=d HEAD)
$new = @(git -C $root -c core.quotepath=off ls-files --others --exclude-standard)
$files = @(($changed + $new) | Where-Object { $_ -like '*.cs' } | Sort-Object -Unique)
if ($files.Count -eq 0) {
    Write-Output 'No C# file has changed: nothing to format.'
    exit 0
}

# The member-order and line-length rules .editorconfig turns on are the only analyzer fixes that run: the others change
# what code does. Each is a pass of its own, as one pass wraps lines where they were before their members moved.
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

# Each pass names the files it formats, and what it formatted.
$common = @('--include') + $files + @('--verbosity', 'normal')
$passes = @(
    @{ Name = 'Order'; Run = { dotnet format analyzers $solution --diagnostics $orderRules @common } }
    @{ Name = 'Wrap'; Run = { dotnet format analyzers $solution --no-restore --diagnostics $wrapRules @common } }
    @{ Name = 'Style'; Run = { dotnet format style $solution --no-restore --exclude-diagnostics $judged @common } }
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
