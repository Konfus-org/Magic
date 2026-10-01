@echo off
rem Installs (or reinstalls) the Magic "dotnet new" templates under VSTemplates\, which Visual Studio's
rem File > New > Project dialog also lists. Run it again after editing a template.
setlocal
set "root=%~dp0VSTemplates"
for %%T in (MagicGem MagicProject) do (
    dotnet new uninstall "%root%\%%T" >nul 2>&1
    dotnet new install "%root%\%%T" || exit /b 1
)
echo.
echo Installed: magicgem, magicproject. Try: dotnet new magicproject -n MyGame --EnginePath "<engine repository>"
endlocal
