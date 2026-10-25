<#
.SYNOPSIS
Installs the templates for a Magic gem and a Magic project, and records which Magic they build for.

.DESCRIPTION
Installs the templates under tools/VS Templates, or updates them, for dotnet new and the IDEs that list its templates,
Visual Studio's New Project among them: magic-gem and magic-project. A gem or project made from them builds for the
Magic folder the MagicDir environment variable names (src/Core/Sdk/README.md), which this sets to this repository's
Build/bin/<Configuration>/ for the current user. Windows keeps a user's environment variables; Linux and macOS have no
such store, so there it prints the line for the shell's profile instead. A running IDE or shell keeps the environment
it started with, so restart it. Runs from any current directory.

.PARAMETER Configuration
The build of Magic the gems and projects build for: Debug, Optimize or Release.

.EXAMPLE
tools/VS Templates/InstallTemplates.ps1

.EXAMPLE
tools/VS Templates/InstallTemplates.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Optimize', 'Release')]
    [string] $Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$templates = $PSScriptRoot
$separator = [System.IO.Path]::DirectorySeparatorChar
$magicDir = (Join-Path (Join-Path (Join-Path $root 'Build') 'bin') $Configuration) + $separator

# Installing the folder again replaces what an earlier install of it put there.
dotnet new install $templates --force
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
    [System.Environment]::SetEnvironmentVariable('MagicDir', $magicDir, [System.EnvironmentVariableTarget]::User)
    Write-Output "MagicDir is now $magicDir. Restart the IDE or shell that should see it."
}
else {
    Write-Output "Add this line to your shell's profile, then start a new shell: export MagicDir='$magicDir'"
}

if (-not (Test-Path (Join-Path (Join-Path $magicDir 'Sdk') 'Gem.props'))) {
    Write-Output "There is no Magic built in $magicDir yet: build it first (dotnet build Magic.slnx -c $Configuration)."
}

exit 0
