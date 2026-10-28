<#
.SYNOPSIS
Installs Magic's dotnet new templates.

.DESCRIPTION
Installs the templates under tools/VS Templates, or updates them, for dotnet new and the IDEs that list its templates,
Visual Studio's New Project among them. Runs from any current directory.

.EXAMPLE
tools/VS Templates/InstallTemplates.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A native command's exit code is read below, never thrown (PowerShell 7.4 would otherwise throw on it).
$PSNativeCommandUseErrorActionPreference = $false

# Installing the folder again replaces what an earlier install of it put there, templates since removed included.
dotnet new install $PSScriptRoot --force
exit $LASTEXITCODE
