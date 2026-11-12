# Tools

The scripts that build and check Magic are in `tools/Scripts/`, and the analyzer that checks the house rules is in
`tools/Analyzers/` ([Analyzers](#analyzers)). Nothing in `tools/` ships.

Each script is one PowerShell file that runs under both `pwsh` and Windows PowerShell 5.1, so don't use what 5.1
lacks (`&&`, `?:`, `??`). A script runs from any folder and exits `0` when it passes. Its help comment lists its
switches: `Get-Help ./tools/Scripts/Validate.ps1 -Full`.

## Format.ps1

Formats the `.cs`, `.csproj`, `.props` and `.targets` files that differ from the last commit, new files included.
`-All` formats every file. It orders each type's members, applies the code style, then lays the code out with
CSharpier. A local build formats too, and `Validate.ps1` checks. Last, it builds the solution and removes each project
or package reference nothing compiled uses, which the build otherwise refuses.

- Fixes that would delete, invent or rename code are left to you. The build still reports them.
- A reference needed by something other than the code, such as a package that only adds build steps, says
  `TreatAsUsed="true"`, and is kept.
- A project that fails its build holds back those built on it, so a reference only they leave unused is removed on
  the next run.
- After it moves statics, check that no static initializer reads a static declared below it, which would still be
  unset.

## Validate.ps1

Runs the checks a change must pass, in order, and stops at the first failure:

1. **Build:** builds `src/Magic.slnx`. Every warning is an error, and so is a file CSharpier would change.
2. **Test:** runs the tests against that build ([Tests](#tests)).
3. **Headless:** runs the sample project without a window under `--fail-on-error`, types `exit` into it and passes
   when it exits `0`. A run still going after `-Seconds` is killed and fails.
4. **Screenshot:** runs `Screenshot.ps1` on the sample project. Needs a display and a GPU.

`-Only` runs some steps, and `-Project` runs another project.

## Profiling.ps1

Takes, waits for or releases a profiling turn: the time in which one worktree alone builds and runs Magic, so that its
profile is not disturbed. The turn is a file in the repository's git folder that every worktree shares, with the
holder's worktree, its branch and the time the turn ends. `-Minutes`, at most 20, takes it. It first waits up to ten
minutes for runs of Magic, builds and test runs from other worktrees to end. `-Release`, run from the holder's
worktree, ends it, and it ends by itself at its time. `Format.ps1`, `Validate.ps1`, `Screenshot.ps1` and `Run.ps1`
call `-Wait` first, so they wait while another worktree holds a turn.

## Run.ps1

Runs a project for an agent, the way an agent starts Magic itself; Screenshot.ps1 and Validate.ps1 run it in their own
way. It names the window with the branch checked out where the script is, or `-Name`, sends `-Commands` as console
lines, and kills a run still going after `-Seconds`. End `-Commands` with `exit` to end the run sooner. `-Headless` runs
without a window and ends at the first error logged. It prints what Magic prints, and doesn't build.

## Screenshot.ps1

Runs a project with a window until its world has loaded, takes a screenshot and prints its path. It doesn't build.
The window's title is the branch checked out where the script is, or `-Name`.

## AgentTaskComplete.ps1

Runs `Format.ps1`, then the steps of `Validate.ps1` that `-Change` needs: `Docs` builds, `Code` adds Test and
Headless, and `Visual` adds Test and Screenshot. If they pass, it stamps the checkout's exact files with the level,
and a state already stamped returns at once. It also points git at the repository's [hooks](#git-hooks).

## Claim.ps1

Claims the files and folders a slice will change, in the repository's git folder, which every worktree shares, and
prints any other claim or worktree that touches them; it exits `1` when one does. `-Release` takes the claim back.
The SessionStart agent hook lists the claims, and the edit agent hook refuses an edit inside another session's claim
([AGENTS.md](../AGENTS.md#worktrees)).

## Templates

`tools/VS Templates/` holds the `dotnet new` templates for a gem and a sample project, and `InstallTemplates.ps1`
installs them. Run it once, and again after a template changes. Each template is a project in the solution's
`Tools/VS Templates` folder and builds where it is, so a template that no longer compiles fails the build.

To add a gem:

- **Visual Studio:** right-click the solution, choose Add > New Project > Magic Gem, give it a name and set its
  location to `src/Gems`.
- **Command line:** `Magic new gem Foo -o src/Gems/Foo` from the repository's root, which runs
  `dotnet new magic-gem -n Foo -o src/Gems/Foo` and adds it to the solution's `Gems` folder.

Either makes `src/Gems/Foo/Foo.csproj` and `FooGem.cs`. It builds to `Gems/Foo/` beside Magic, and a project
runs it by listing `Foo` in its `.magic` file. Its csproj states its `Description`, `Version` and `Authors`.

To add a sample project:

- **Visual Studio:** right-click the `Samples` solution folder, choose Add > New Project > Magic Project, give it a
  name and set its location to the repository's `samples` folder.
- **Command line:** `Magic new project Foo -o samples/Foo` from the repository's root, then
  `dotnet sln src/Magic.slnx add samples/Foo/Samples.Foo.csproj --solution-folder Samples`.

Either makes `samples/Foo/Foo.magic`, listing no gems, and `Samples.Foo.csproj`, whose Run builds Magic and opens the
project ([Samples](../README.md#samples)).

## Analyzers

`tools/Analyzers/` holds the rules of the [Code Standards](CODE_STANDARDS.md) that no built-in rule checks, as a Roslyn
analyzer every other project in the solution runs through `Directory.Build.props`. A rule's id is `MG` and a number;
each is one class whose summary says what it checks and names the standard, and the build and the editor report it
like any other rule. `.editorconfig` sets the severities.

Every rule has a code fix, in a class beside it named for the rule with `CodeFix` in place of `Analyzer`, so the
editor offers it and `tools/Scripts/Format.ps1` can apply it. A rule whose fix would need a decision the author must
make still has one: it does the mechanical part and leaves the decision visible.

To add a rule: the analyzer and its fix, a line in `AnalyzerReleases.Unshipped.md`, the severity in `.editorconfig`,
and the standard it enforces named in its summary. Visual Studio keeps the analyzer it loaded; restart it after a
change here.

## Git hooks

Git runs the hooks in `.githooks/` once `core.hooksPath` names that folder: `git config core.hooksPath .githooks`,
once per clone, which `tools/Scripts/AgentTaskComplete.ps1` also sets ([Commits](../AGENTS.md#commits)).

- `prepare-commit-msg` starts a commit's subject with `type: `, such as `feat: `. The type comes from the branch's
  name, or from the staged files when they are all Markdown, CI, build or test files.
- `commit-msg` refuses a commit whose subject still doesn't start with `type: `, or has a scope such as
  `feat(core): `.

## Tests

The unit tests are in `tests/`, one project per gem or library under test, named `<Gem>.Tests`. They run on xunit
through the Microsoft testing platform, which `global.json` selects.

- **Run them all:** `dotnet test --solution src/Magic.slnx`.
- **Run one project:** `dotnet test --project tests/<Gem>.Tests/<Gem>.Tests.csproj`.
- A test project sees the gem's internal members through `InternalsVisibleTo` in the gem's project file.
- A test builds what it checks from small data in the test itself, never from an asset on disk.
