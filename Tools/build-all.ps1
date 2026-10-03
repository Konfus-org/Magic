# Builds the host, every gem and every sample, for one platform. The solution cannot take a runtime ("dotnet build
# Magic-Wizard.slnx -r linux-x64" is refused by the SDK), so this builds the projects one by one with it; the output
# lands where Directory.Build.props puts that runtime (Build\net10.0\Debug-linux-x64\ for a Linux build on Windows).
# Runs under pwsh on any OS, and under Windows PowerShell.
#
#   Tools\build-all.ps1                               the machine's own platform, Debug
#   Tools\build-all.ps1 -Runtime linux-x64            what the "(WSL)" launch profiles run
#   Tools\build-all.ps1 -Runtime linux-x64 -Configuration Release
param(
    [string] $Runtime = '',
    [string] $Configuration = 'Debug'
)

$root = Split-Path $PSScriptRoot -Parent
$projects = @(Join-Path $root 'Magic/Magic.csproj')
$projects += Get-ChildItem (Join-Path $root 'Gems') -Filter *.csproj -Recurse -Depth 1 | ForEach-Object { $_.FullName }
$projects += Get-ChildItem (Join-Path $root 'Samples') -Filter *.csproj -Recurse -Depth 1 | ForEach-Object { $_.FullName }

$arguments = @('-c', $Configuration, '-nologo', '-v', 'q')
if ($Runtime -ne '') { $arguments += @('-r', $Runtime) }

foreach ($project in $projects) {
    Write-Host "Building $(Split-Path $project -Leaf)"
    dotnet build $project @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
