# Tools

The scripts in `tools/Scripts/` build and check Magic. Nothing in `tools/` ships.

`tools/Blender/` holds a Blender add-on that reads a domain of a Magic project into Blender and writes it back:
File > Import > Magic (.magic) and File > Export > Magic (.magic). Object Properties > Magic holds the components of
an entity. The reference tools build their Cycles scene with its import
([Reference views](DEBUGGING.md#reference-views)). The add-on runs the Magic program that a person sets in its
preferences and builds nothing. How to install it, how to set that program, and what the export writes and skips,
are in [its README](../tools/Blender/README.md).

## Scripts

Each script is one PowerShell file. It runs under `pwsh` and Windows PowerShell 5.1, from any folder. It exits `0`
when it passes.

The help comment of a script lists its parameters: `Get-Help ./tools/Scripts/Validate.ps1 -Full`.

When you write a script, do not use `&&`, `?:` or `??`. Windows PowerShell 5.1 does not have them.

## Traps

Shells:

- If `pwsh` is not installed, run the scripts with `powershell`.
- Do not run `wsl` or a bare `bash` from PowerShell. It starts the WSL virtual machine.
- `powershell -File <script> -Commands 'a', 'b'` joins the list into one string. Use
  `powershell -Command "& <script> -Commands 'a', 'b'"`.
- In Windows PowerShell 5.1, `$PSScriptRoot` is empty in a default value of `param`. Set such a default after the
  `param` block.
- Git Bash changes a switch such as `-bl:/c/...` into a wrong path. Give MSBuild path switches from PowerShell.

Git:

- Run `git commit` and `git merge` in a command of their own. The agent hooks read the folder of the first `git -C`
  or `cd` in the command. Do not send the output of a merge through `tail`, which hides its failure.
- Do not run `git add -N .` to get a diff. It stages the deletion of tracked files.
- All the worktrees share one stash list. Do not run a bare `git stash pop`.
- After a build, `git worktree remove` can fail with "Permission denied". It still removes the worktree from git. An
  idle build server holds the empty folder: run `dotnet build-server shutdown`, then delete the folder.
- The root `.gitignore` ignores each folder with the name `Logs`, `Cache` or `Screenshots`, at any depth.

Builds and runs:

- Build a new worktree one time before the first run of `Format.ps1` or `AgentTaskComplete.ps1`. Before the build,
  the generated bindings do not exist, and the format removes the `using` lines for them.
- `Run.ps1` and `Screenshot.ps1` do not build. Build after each edit, before the run that checks it.
- The build does not compile `.slang` files. Run Magic to check a shader edit: `Validate.ps1 -Only Headless`.
- A Magic that the user started in a terminal holds `Magic.exe`, and the build then fails at its copy step. Ask the
  user to close it.
- The camera of the samples turns. Two screenshots at different times show different views.

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
3. **Headless:** runs all the sample projects at the same time, without a window, under `--fail-on-error`. Each run
   types `exit` when its world loads. The step passes when each run exits `0`. A run that is not done after `-Seconds`
   fails. A run that fails prints its last output.
4. **Screenshot:** runs `Screenshot.ps1` on each sample project. This step needs a display and a GPU.

The cabin sample builds its acceleration structure over all its entities in the first frame after a cold cache, and
Windows can stop the device for it. Run the first `Headless` or `Screenshot` step after a cold cache one more time
before you report it as a failure.

`-Only` runs only the steps that you name. `-Project` runs other projects.

## AgentTaskComplete.ps1

Runs `Format.ps1`, then the steps of `Validate.ps1` that the `-Change` level needs ([Commands](../AGENTS.md#commands)).
If the change touches only files in sample folders, Markdown aside, only those samples run.

At the `Docs` level the script runs nothing and only stamps. It accepts Markdown, the files in
`tools/Scripts/`, `.claude/` and `.githooks/`, and a `.cs` or `.slang` file in which each changed line is a `//`
comment. It refuses a change that has another file.

If the steps pass, the script stamps the exact files of the checkout with the level. If the state already has a stamp
at that level or higher, the script stops at once. After a pull that git merged with no conflict, of a stamped state
into a stamped state, the script runs only the build.

## Run.ps1

Runs a project for an agent, and names the window with the branch of the worktree. The window has that name from the
moment it opens, because the script gives the name to Magic with `--name`. End `-Commands` with `exit` to end the run.
The script does not build.

The window opens on the display after the main one, behind the window that has the focus, so that it does not interrupt
the user. Use `-Display` to select a different display. `Screenshot.ps1` does the same with the name and the display.

The project's name shows in the title bar when the run gives no `--name`.

## Screenshot.ps1

Runs a project with a window until its world loads. Then it takes a screenshot and prints the path of the image. The
script does not build.

The screenshot shows the scene only. To photograph a failure:

- `-AllowErrors` lets a run that logs errors go on and take its screenshots. The script prints the `[Error]` lines of
  the run before the paths.
- `-Window` also captures the client area of the window to a PNG next to each screenshot, with `-window` in its name.
  The capture shows what is drawn onto the window too, such as the banner of a failure and the debug UI. It copies
  the screen, so keep other windows off the window of Magic. It needs Windows.

The camera of the sample turns. To stop it and face the spawn direction, send these lines with `-Commands`. The
whole component is written, so the line names the camera's own script beside the one that turns it. A value in JSON
goes in single quotes, which is what keeps its double quotes, and each of those single quotes is doubled inside the
single-quoted line of PowerShell:

```powershell
tools/Scripts/Screenshot.ps1 -AllowErrors -Window -Commands 'set player.scriptcontainer ''[{"id":100411,"degreesPerSec":0},{"id":100412}]''', 'set player.transform.rotation 0.2164 0 0 0.9763'
```

## VisualTests.ps1

Runs the visual tests of the cabin against its Blender Cycles references and prints PASS or FAIL for each view, hour
and limit, with the paths of the AgX split pictures and of the report page.

```pwsh
tools/Scripts/VisualTests.ps1                                      # every view at each of its hours
tools/Scripts/VisualTests.ps1 -View cabin-wall,cabin-outside -Hour 12,22
tools/Scripts/VisualTests.ps1 -View cabin-outside -Hour 12 -Tier Quick
```

`-Tier Quick` takes the still frames and the two camera angles beside the view. `-Tier Full`, the default, takes the
turns, the strafe and the light change as well, and so it judges the settle rows and the noise of a move too.

`-UpdateBaseline` writes the baseline again from this run. `-Repeat <n>` runs each view and hour n times and prints
the spread of each row. What a verdict beside PASS or FAIL means, and what the command's exit code says, are in
[Reference views](DEBUGGING.md#reference-views).

The views, their hours and the world points it measures are in `tools/Scripts/ReferenceViews.json`, and the limits
are in `tools/Scripts/VisualTests.json`. It needs Blender. Give its path with `-Blender` when it is not the default
one. What it runs, and how to read each picture and each verdict that it writes, is in
[Reference views](DEBUGGING.md#reference-views).

## JudgeVisualTests.ps1

Judges the rows of a run of `VisualTests.ps1` against the limits, prints a table of each view and hour, and writes
the report page: one HTML file with each table and the pictures of each measure, the failing and the worse rows
first. The page asks nothing of the network and runs no script, so it opens from the file alone. `VisualTests.ps1`
calls it at the end of a run, and writes `rows.json` beside the folders of the run for it. Run it by hand on the
same folder after a change to a limit or a tolerance, and the run is judged again with no run of Magic:

```pwsh
tools/Scripts/JudgeVisualTests.ps1 -Run <the folder of a run>
```

It needs no Blender and no Magic.

## UpdateReferences.ps1

Renders the Blender Cycles reference of each view and hour of the cabin scene and stores it in the repository. This
is the one script that runs Cycles. Run it by hand after a change to the scene: the materials, the models, the
lights, the camera of a view, the hours, or the code that builds the scene in Blender.

```pwsh
tools/Scripts/UpdateReferences.ps1                                   # what is missing or stale
tools/Scripts/UpdateReferences.ps1 -View cabin-room -Hour 12 -Samples 64
tools/Scripts/UpdateReferences.ps1 -All                              # every view and hour again
```

`tools/Reference/References` holds one reference of each view and hour, and git keeps them with Git LFS. A new clone
needs `git lfs install` one time, and the checkout step of a CI job needs `lfs: true`. What each stored file holds is
in [Reference views](DEBUGGING.md#reference-views).

It needs Blender. Give its path with `-Blender` when it is not the default one. It builds Magic one time and gives
Blender the program of that build, which Blender starts with no window.

## Profiling.ps1

Takes, waits for or releases a profiling turn. In a profiling turn, only one worktree runs Magic and the tests, so
that nothing disturbs its measurements. The scripts that run Magic or the tests wait while another worktree holds a
turn. On Windows, `Format.ps1` and the build of `Validate.ps1` run at once, at idle priority. On other systems, they
wait.

`-Slot` waits for a free run slot, and gives the slot to the script that asked for it. Only `MAGIC_RUN_SLOTS` runs of
Magic or of the tests go at the same time, from all the sessions, and only one run if the variable is not set.
`Run.ps1`, `Screenshot.ps1` and the test and headless steps of `Validate.ps1` hold a slot while they run. Thus the
sessions build and write code at the same time, but they do not run Magic at the same time. A slot is a file in the
git folder that its holder keeps open. The system frees the slot when the holder stops, and thus a run that is killed
does not keep its slot. The wait for a slot ends after the time in the script, with a message that names the holder.

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

The template makes `src/Gems/Foo/Foo.csproj` and `EntryPoint.cs`. It also makes the settings of the gem,
`FooSettings.cs`, and a part that logs that the gem runs. It adds the project to the `Gems` folder of the solution. The
csproj of the gem states its `Description`, `Version` and `Authors`.

`Magic new <template> <name>` runs `dotnet new magic-<template>`. It passes on each option that Magic does not know,
such as `-o`.

### Make a sample project

1. From the root of the repository, run `Magic new project Foo -o samples/Foo`.
2. Run `dotnet sln src/Magic.slnx add samples/Foo/Samples.Foo.csproj --solution-folder Samples/Foo`.
3. In `src/Magic.slnx`, add `<File Path="../samples/Foo/Foo.magic" />` to the `/Samples/Foo/` folder.

Or, in Visual Studio:

1. Right-click the `Samples` solution folder and select Add > New Solution Folder. Give it the name of the sample.
2. Right-click the new folder and select Add > New Project > Magic Project.
3. Set its location to the `samples` folder of the repository.
4. Right-click the new folder and select Add > Existing Item. Select the `.magic` file of the sample.

The template makes `samples/Foo/Foo.magic`, which lists no gems, and `Samples.Foo.csproj`. When you run the csproj, it
builds Magic and opens the project ([Samples](../README.md#samples)).

The `.magic` file can also name these assets, each as `{ "id": <asset id> }`:

- `entryPoint`: the domain that the run opens first. If it is not set, the run opens the default domain.
- `loadingScreen`: the domain that shows while a domain replaces the world. If it is not set, the engine's loading
  screen shows.
- `player`: the prefab that the run puts at the spawn of a domain. If it is not set, the run puts nothing there.

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

The commands that run the tests, and the rules for tests, are in [Tests](TESTS.md).
