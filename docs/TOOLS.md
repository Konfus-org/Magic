# Tools

The scripts in `tools/Scripts/` build and check Magic. Nothing in `tools/` ships.

## Scripts

Each script is one PowerShell file. It runs under `pwsh` and Windows PowerShell 5.1, from any folder. It exits `0`
when it passes.

The help comment of a script lists its parameters: `Get-Help ./tools/Scripts/Validate.ps1 -Full`.

When you write a script, do not use `&&`, `?:` or `??`. Windows PowerShell 5.1 does not have them.

## Format.ps1

Formats the `.cs`, `.csproj`, `.props` and `.targets` files that are different from the last commit, and new files.
`-All` formats all the files.

The script puts the members of each type in order, applies the code style and lays out the code with CSharpier. Then
it builds the solution and removes each project or package reference that no code uses.

- The script does not apply a fix that deletes, adds or renames code. The build still reports it.
- If something other than the code needs a reference, mark the reference `TreatAsUsed="true"`. An example is a package
  that only adds build steps. The script keeps it.
- If a project does not build, the script does not check the projects that depend on it. The next run removes the
  references that they do not use.
- After the script moves static members, make sure that no static initializer reads a static that is declared below
  it. That static is not set yet.

## Validate.ps1

Runs the checks that a change must pass. It runs these steps in order, and stops at the first that fails:

1. **Build:** builds `src/Magic.slnx`. Each warning is an error. A file that CSharpier would change is an error too.
2. **Test:** runs the tests against that build ([Tests](#tests)).
3. **Headless:** runs each sample project without a window under `--fail-on-error`, then types `exit`. The step passes
   when the run exits `0`. A run that is not done after `-Seconds` fails.
4. **Screenshot:** runs `Screenshot.ps1` on each sample project. This step needs a display and a GPU.

`-Only` runs only the steps that you name. `-Project` runs other projects.

## AgentTaskComplete.ps1

Runs `Format.ps1`, then the steps of `Validate.ps1` that the `-Change` level needs ([Commands](../AGENTS.md#commands)).

If the steps pass, the script stamps the exact files of the checkout with the level. If the state already has a stamp
at that level or higher, the script stops at once.

## Run.ps1

Runs a project for an agent, and names the window with the branch of the worktree. End `-Commands` with `exit` to end
the run. The script does not build.

## Screenshot.ps1

Runs a project with a window until its world loads. Then it takes a screenshot and prints the path of the image. The
script does not build.

## Profiling.ps1

Takes, waits for or releases a profiling turn. In a profiling turn, only one worktree builds and runs Magic, so that
nothing disturbs its measurements. The scripts that build or run Magic wait while another worktree holds a turn.

## Claim.ps1

Claims the files and folders that a slice will change. All the worktrees see the claim. An agent hook refuses an edit
inside the claim of another session ([Worktrees](../AGENTS.md#worktrees)).

## Templates

`tools/VS Templates/` holds the `dotnet new` templates for a gem and a sample project. Each template is a project in
the solution, so a template that does not compile fails the build.

To install the templates, run `tools/VS Templates/InstallTemplates.ps1`. Run it again after a template changes.

### Make a gem

From the root of the repository, run `Magic new gem Foo -o src/Gems/Foo`. Or, in Visual Studio:

1. Right-click the solution.
2. Select Add > New Project > Magic Gem.
3. Give the gem a name.
4. Set its location to `src/Gems`.

The template makes `src/Gems/Foo/Foo.csproj` and `FooGem.cs`, and adds the project to the `Gems` folder of the
solution. The csproj of the gem states its `Description`, `Version` and `Authors`.

`Magic new <template> <name>` runs `dotnet new magic-<template>`. It passes on each option that Magic does not know,
such as `-o`.

### Make a sample project

1. From the root of the repository, run `Magic new project Foo -o samples/Foo`.
2. Run `dotnet sln src/Magic.slnx add samples/Foo/Samples.Foo.csproj --solution-folder Samples`.

In Visual Studio, right-click the `Samples` solution folder and select Add > New Project > Magic Project. Set its
location to the `samples` folder of the repository.

The template makes `samples/Foo/Foo.magic`, which lists no gems, and `Samples.Foo.csproj`. When you run the csproj, it
builds Magic and opens the project ([Samples](../README.md#samples)).

## Analyzers

`tools/Analyzers/` holds a Roslyn analyzer with the rules of the [Code Standards](CODE_STANDARDS.md) that no built-in
rule checks. Each other project in the solution runs it, through `Directory.Build.props`.

- The id of a rule is `MG` and a number. The summary of the rule class tells what it checks and names the standard.
- The build and the editor report the rules as they report other rules. `.editorconfig` sets their severities.
- Each rule has a code fix in a class next to it. The class has the name of the rule, with `CodeFix` in place of
  `Analyzer`. The editor offers the fix, and `Format.ps1` can apply it.
- If a fix needs a decision from the author, the fix does the mechanical part and leaves the decision visible.

To add a rule:

1. Write the analyzer and its code fix.
2. Name the standard that the rule checks in the summary of the analyzer.
3. Add a line to `AnalyzerReleases.Unshipped.md`.
4. Set the severity in `.editorconfig`.
5. Restart Visual Studio. It keeps the analyzer that it loaded first.

## Git hooks

Git runs the hooks in `.githooks/` when `core.hooksPath` names that folder. Run `git config core.hooksPath .githooks`
once for each clone. `AgentTaskComplete.ps1` also sets it ([Commits](../AGENTS.md#commits)).

- `prepare-commit-msg` starts the subject of a commit with `type: `, such as `feat: `. The type comes from the name of
  the branch. If all the staged files are Markdown, CI, build or test files, the type comes from them.
- `commit-msg` refuses a commit if its subject does not start with `type: `, or if it has a scope such as
  `feat(core): `.

## Tests

The unit tests are in `tests/`. There is one project for each gem or library under test, with the name
`<Gem>.Tests`. The tests use xunit on the Microsoft testing platform, which `global.json` selects.

- To run all the tests, run `dotnet test --solution src/Magic.slnx`.
- To run one project, run `dotnet test --project tests/<Gem>.Tests/<Gem>.Tests.csproj`.
- A test project sees the internal members of its gem through `InternalsVisibleTo` in the csproj of the gem.
- A test makes the data that it checks in the test itself. It never reads an asset from disk.
