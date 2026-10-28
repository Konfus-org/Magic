# Tools

The scripts that build and check Magic are in `Scripts/`, and the analyzer that checks the house rules is in
[`Analyzers/`](Analyzers/Magic.Analyzers/README.md). Nothing here ships.

Each script is one PowerShell file that runs under both `pwsh` and Windows PowerShell 5.1, so don't use what 5.1
lacks (`&&`, `?:`, `??`). A script runs from any folder and exits `0` when it passes. Its help comment lists its
switches: `Get-Help ./tools/Scripts/Validate.ps1 -Full`.

There is no sample project yet. Until there is, only the Build and Test steps work: run
`Validate.ps1 -Only Build,Test`, and `AgentTaskComplete.ps1` at the `Docs` level.

## Format.ps1

Formats the `.cs`, `.csproj`, `.props` and `.targets` files that differ from the last commit, new files included.
`-All` formats every file. It orders each type's members, applies the code style, then lays the code out with
CSharpier. A local build formats too, and `Validate.ps1` checks.

- Fixes that would delete, invent or rename code are left to you. The build still reports them.
- After it moves statics, check that no static initializer reads a static declared below it, which would still be
  unset.

## Validate.ps1

Runs the checks a change must pass, in order, and stops at the first failure:

1. **Build:** builds `Magic.slnx`. Every warning is an error, and so is a file CSharpier would change.
2. **Test:** runs the tests against that build.
3. **Headless:** runs the sample project without a window under `--fail-on-error`.
4. **Screenshot:** runs `Screenshot.ps1` on the sample project. Needs a display and a GPU.

`-Only` runs some steps, and `-Project` runs another project.

## Screenshot.ps1

Runs a project with a window until its world has loaded, takes a screenshot and prints its path. It doesn't build.

## AgentTaskComplete.ps1

Runs `Format.ps1`, then the steps of `Validate.ps1` that `-Change` needs: `Docs` builds, `Code` adds Test and
Headless, and `Visual` adds Test and Screenshot. If they pass, it stamps the checkout's exact files with the level,
and a state already stamped returns at once. It also points git at the repository's [hooks](../.githooks/README.md).

## Templates

`VS Templates/` holds the `dotnet new` template for a Core module, and `InstallTemplates.ps1` installs it.

To add a Core module, run `InstallTemplates.ps1` once, then:

- **Visual Studio:** right-click the `Core` solution folder, choose Add > New Project > Magic Core Module, give it
  the bare name (`Assets`, not `Magic.Assets`) and set its location to `src`.
- **Command line:** `dotnet new magic-core -n Assets -o src/Assets`, then
  `dotnet sln Magic.slnx add src/Assets/Magic.Assets.csproj --solution-folder Core`.

Either makes `src/Assets/Magic.Assets.csproj` and its README. Its settings come from `Directory.Build.props`, so the
project file lists only its references. Add those, and the folder's line in the [README](../README.md#folders).
