# Tools

The scripts that build and check Magic are in `Scripts/`, the analyzer that checks the house rules in
[`Analyzers/`](Analyzers/Magic.Analyzers/README.md), and the templates for a new gem or project in `VS Templates/`. Nothing here
ships.

Each script is one PowerShell file that runs under both `pwsh` and Windows PowerShell 5.1: don't use what 5.1 lacks
(`&&`, `?:`, `??`). A script runs from any folder and exits `0` when it passes. Its help comment lists its switches
(`Get-Help ./tools/Scripts/Validate.ps1 -Full`). Scripts that run Magic use `Build/bin/<Configuration>/Magic`,
`Debug` unless `-Configuration` says otherwise. Build first.

## Format.ps1

Formats the `.cs`, `.csproj`, `.props` and `.targets` files that differ from the last commit, new files included.
`-All` formats every file. It puts each type's members in order, applies the code style, then prints the layout with
CSharpier, which the build checks. It builds nothing.

- Fixes that would delete, invent or rename code are left to you. The build still reports them.
- Check moved statics in review: a static initializer that reads a static declared below it reads it unset. Make a
  static that other statics read a field, with a property over it if other types read it.
- CSharpier has no options beyond the width, indent and line ending in `.editorconfig`; what it prints is final.

## Validate.ps1

Runs the checks every change must pass, in order, and stops at the first failure:

1. **Build:** builds `Magic.slnx`. Every warning is an error, and a file CSharpier would change fails it.
2. **Test:** runs the tests against that build.
3. **Headless:** runs the samples without a window under `--fail-on-error` and opens every domain.
4. **Screenshot:** runs `Screenshot.ps1` through every domain. Needs a display and a GPU.

It prints each step's exit code and the screenshots' paths. Open and look at each one. `-Only Build,Headless` runs
some steps, `-Project` runs another project, and `-Seconds` sets how long each domain may take to load.

## Screenshot.ps1

Runs a project with a window until its world has loaded and settled, takes a screenshot and prints its path. It
doesn't build. `$png = tools/Scripts/Screenshot.ps1` keeps the paths. `-Domains` then opens each domain it names and
screenshots it, `-Commands` sends console lines first (`'set RealtimeGIRendering.renderScale=1'` changes a setting), and
`-Project` runs another project.

## AgentTaskComplete.ps1

An agent runs it when a task is done, with `-Change` saying what the change can affect. It runs `Format.ps1`, then
the steps of `Validate.ps1` that level needs:

- `Docs`: Build.
- `Code`: Build, Test and Headless.
- `Visual`: Build, Test and Screenshot.

If every step passes, it stamps the checkout's exact files with the level and prints any screenshots' paths. A state
already stamped at that level or a higher one returns at once.

Run it again after any edit or rebase. With `-Hook` it instead checks a `git commit` or `git merge` named on stdin
against the stamps, for a tool hook that guards commits. It also sets `core.hooksPath` to `.githooks` when the clone
has none, so the repository's hooks run on every commit ([Git hooks](../.githooks/README.md)).

## Templates

`VS Templates/` holds the `dotnet new` templates for your own gem (`magic-gem`) and project (`magic-project`). What they make builds into the
`Gems/` or `Projects/` folder of the Magic that `MagicDir` names, and running a project starts that Magic on it
([Building for Magic](../src/Core/Sdk/README.md)). A new project gets random asset ids.

## InstallTemplates.ps1

Installs or updates the templates for `dotnet new` and IDEs such as Visual Studio. It also sets `MagicDir` for the
current user to this repository's `Build/bin/<Configuration>/`, `Debug` unless `-Configuration` says otherwise. On
Linux and macOS it prints the line to add to your shell profile instead. Restart your IDE or shell afterwards.
