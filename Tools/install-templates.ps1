# Installs (or reinstalls) the Magic "dotnet new" templates under VSTemplates, which Visual Studio's
# File > New > Project dialog also lists. Run it again after editing a template. Runs under pwsh on any OS,
# and under Windows PowerShell.
foreach ($template in 'MagicGem', 'MagicProject', 'MagicSample') {
    $path = Join-Path (Join-Path $PSScriptRoot 'VSTemplates') $template

    dotnet new uninstall $path *> $null
    dotnet new install $path
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

Write-Host ''
Write-Host 'Installed: magicgem, magicproject, magicsample. Try: dotnet new magicproject -n MyGame --EnginePath "<engine repository>"'
