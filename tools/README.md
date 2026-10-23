# Tools

Scripts that build and check Magic, the repository's own analyzers, and the templates for a new gem or project.
Nothing here ships.

Each script is one PowerShell file that runs under both `pwsh` and Windows PowerShell 5.1: don't use what 5.1 lacks
(`&&`, `?:`, `??`). A script runs from any folder and exits `0` when it passes. Its help comment lists its switches
(`Get-Help ./tools/Validate.ps1 -Full`). Scripts that run Magic use `Build/bin/<Configuration>/Magic`, `Debug` unless
`-Configuration` says otherwise. Build first.

## Format.ps1

Formats the `.cs` files that differ from the last commit, new files included, by the rules in `.editorconfig`. `-All`
formats every file. It orders members, applies the code style, lays out blank lines and `if`/`else` bodies
([Analyzers](#analyzers)), wraps long lines and lists, and fixes whitespace. It builds only the analyzers.

- Fixes that would delete, invent or rename code are left to you. The build still reports them.
- It is the only check of what the build can't see: a name that can be shorter (IDE0001), and a file's charset and
  final newline.
- Check moved statics in review: a static initializer that reads a static declared below it reads it unset. Make a
  static that other statics read a field, with a property over it if other types read it.
- The wrap never joins lines back. Reshape a wrap that reads badly by hand, and run it again when a fix makes another
  possible. Split a long string with `+`, and wrap a comment at a word.

## Validate.ps1

Runs the checks every change must pass, in order, and stops at the first failure:

1. **Build:** builds `Magic.slnx`. Every warning is an error, including every rule `Format.ps1` applies that a build
   can see.
2. **Test:** runs the tests against that build.
3. **Headless:** runs the samples without a window under `--fail-on-error` and opens every domain.
4. **Screenshot:** runs `Screenshot.ps1` through every domain. Needs a display and a GPU.

It prints each step's exit code and the screenshots' paths. Open and look at each one. `-Only Build,Headless` runs
some steps, `-Project` runs another project, and `-Seconds` sets how long each domain may take to load.

## Screenshot.ps1

Runs a project with a window until its world has loaded and settled, takes a screenshot and prints its path. It
doesn't build. `$png = tools/Screenshot.ps1` keeps the paths. `-Domains` then opens each domain it names and
screenshots it, `-Commands` sends console lines first (`'set VoxelRendering.renderScale=1'` changes a setting), and
`-Project` runs another project.

## AgentTaskComplete.ps1

An agent runs it when a task is done, with `-Change` saying what the change can affect. It runs `Format.ps1`, then
the steps of `Validate.ps1` that level needs:

- `Docs`: Build.
- `Code`: Build, Test and Headless.
- `Visual`: Build, Test and Screenshot.

If every step passes, it stamps the checkout's exact files with the level and prints any screenshots' paths. A state
already stamped at that level or a higher one returns at once.

`.claude/settings.json` runs it with `-Hook` before an agent's `git commit` or `git merge`. The hook refuses the
command unless that exact state was stamped. Run it again after any edit or rebase.

## Templates

`dotnet new` templates for your own gem (`magic-gem`) and project (`magic-project`). What they make builds into the
`Gems/` or `Projects/` folder of the Magic that `MagicDir` names, and running a project starts that Magic on it
([Building for Magic](../src/Core/Sdk/README.md)). A new project gets random asset ids.

## InstallTemplates.ps1

Installs or updates the templates for `dotnet new` and IDEs such as Visual Studio. It also sets `MagicDir` for the
current user to this repository's `Build/bin/<Configuration>/`, `Debug` unless `-Configuration` says otherwise. On
Linux and macOS it prints the line to add to your shell profile instead. Restart your IDE or shell afterwards.

## Analyzers

`Analyzers` and `Analyzers.CodeFixes` are the repository's own analyzers and their fixes, for rules no package or the
SDK has. Every project runs them as it builds, and `Format.ps1` applies their fixes. Each rule's summary says what it
asks. Turn a new rule on in `.editorconfig`. Visual Studio picks up a changed analyzer when the solution is reopened.
