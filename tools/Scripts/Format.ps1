<#
.SYNOPSIS
Formats the files a change touches: puts each type's members in order, applies the code style, then prints the layout
with CSharpier.

.DESCRIPTION
The files are the .cs, .csproj, .props and .targets files that differ from HEAD, staged, unstaged or new, so the
change being made is formatted and no other code is touched; -All formats every file, for when a rule is added. Three
passes, in this order: the member-order fix (tools/Analyzers, MG0001) through dotnet format, so members move before
anything else is measured; the code-style fixes through dotnet format, except the few that need a person's judgment,
which the build still reports; and CSharpier, which decides every line break, indent and blank line from
.editorconfig's [*] block and is what the build checks (Directory.Build.props). The first two read the code as the
last build left it and build nothing. tools/Scripts/AgentTaskComplete.ps1 runs this first, so an agent's change is
formatted before it is validated. Runs from any current directory.

.PARAMETER All
Formats every file in the repository rather than the changed ones.

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

# dotnet format takes the C# files to touch; CSharpier takes every changed file it formats, or the repository.
$common = @('--verbosity', 'normal')
$csharpierPaths = @('.')
if (-not $All) {
    $changed = @(git -C $root -c core.quotepath=off diff --name-only --diff-filter=d HEAD)
    $new = @(git -C $root -c core.quotepath=off ls-files --others --exclude-standard)
    $files = @(($changed + $new) | Where-Object { $_ -match '\.(cs|csproj|props|targets)$' } | Sort-Object -Unique)
    if ($files.Count -eq 0) {
        Write-Output 'No file has changed: nothing to format.'
        exit 0
    }
    $csharpierPaths = $files
    $csFiles = @($files | Where-Object { $_ -like '*.cs' })
    if ($csFiles.Count -gt 0) { $common = @('--include') + $csFiles + $common }
}

# Style fixes that delete code, invent it or rename it across the solution, which a person decides: missing switch
# cases, unreachable code, unused members, values and parameters, and names.
$judged = 'IDE0010', 'IDE0072', 'IDE0035', 'IDE0051', 'IDE0052', 'IDE0059', 'IDE0060', 'IDE1006'

$passes = @(
    @{ Name = 'Order'; Run = { dotnet format analyzers $solution --diagnostics MG0001 @common } }
    @{ Name = 'Style'; Run = { dotnet format style $solution --no-restore --exclude-diagnostics $judged @common } }
    @{ Name = 'CSharpier'; Run = { dotnet csharpier format @csharpierPaths } }
)

# The included paths are relative to the repository, as git gave them.
Push-Location $root
try {
    foreach ($pass in $passes) {
        if ($pass.Name -ne 'CSharpier' -and -not $All -and $csFiles.Count -eq 0) { continue }
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
