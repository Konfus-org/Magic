# Tools

What the build runs, what checks a change, and what a gem or project of one's own starts from; nothing here ships. A
tool is a `dotnet` project the build invokes, such as the bindings generator, or one cross-platform PowerShell script
that runs the same under `pwsh` on Linux and macOS and under Windows PowerShell 5.1, so it avoids what 5.1 lacks
(`&&`, `?:`, `??`). A script runs from any current directory, finds the repository from its own folder, and reports
with its exit code: 0 passed, else the failing command's code.

The scripts run the `Magic` the build made, `Build/bin/<Configuration>/Magic` under the repository (`Magic.exe` on
Windows): the `Debug` one unless `-Configuration` says `Optimize` or `Release`. A script that finds none says to build
first.

## Validate.ps1

`Validate.ps1` runs the checks [AGENTS.md](../AGENTS.md) asks of every change, in order, and stops at the first failure:
builds the solution, where every warning is already an error; runs the tests against that build; runs the samples
headless under `--fail-on-error`, which exits 2 when any error was logged; and runs them again with a window, which
needs a display and a GPU. It ends with one line per step and its exit code, then `PASSED` or which step failed. `-Only`
picks the steps when the rest already passed (`Test` alone needs a build to be there), `-Frames` how long each sample
run lasts, `-Configuration` which build, and `-Project` another project than the samples.

The build is incremental and never starts over: what is built is reused, and the native libraries are built once
per machine, in a folder every checkout and worktree shares
([Native libraries](../src/Bundled/Gems/README.md#native-libraries)), so a fresh worktree builds in minutes. It builds
with MSBuild's plain console logger rather than the terminal one, which shows a task's messages only after its
project ends, so which native library is building, and for how long, shows as it happens in a terminal too.

## Screenshot.ps1

`Screenshot.ps1` is the visual check: it runs the project with a window, pipes `wait` and `screenshot` into the
console, and prints the PNG's path as its only output, so `$png = tools/Screenshot.ps1` keeps it. The scene is not
drawn in the first frames, so `-Wait` holds the screenshot back well past them, and `-Frames` must reach past that.
`-Commands` sends console lines before the screenshot, such as choosing a debug view, so what the image shows is
chosen rather than found. It does not build: run the build step first.

## Templates

`Templates/` holds the `dotnet new` templates a user of the engine starts from, wherever on disk the result goes: a
gem, a plugin a project lists, with its settings; and a project, a game, with its `.magic` file and a domain to open
in with a script attached in it. What they make builds for the Magic folder the `MagicDir` environment variable names,
into its `Gems/` or `Projects/` folder, and a project's Run starts that Magic on it
([Building for Magic](../src/Core/Sdk/README.md)). A project's asset ids are drawn at random as it is made, as ids
must be unique across the project, the engine and its gems.

The templates are the engine's conventions shown once, not a framework: what they make is the user's to change, and
a template changes when what a gem or a project needs does.

## InstallTemplates.ps1

`InstallTemplates.ps1` installs the templates, or updates them after a change, for `dotnet new` and the IDEs that list
its templates, Visual Studio's New Project among them, and sets `MagicDir` to this repository's
`Build/bin/<Configuration>/` for the current user, `Debug` unless `-Configuration` says otherwise. Windows keeps a
user's environment variables; on Linux and macOS it prints the line for the shell's profile instead. An IDE or shell
keeps the environment it started with, so it must be started again to see `MagicDir`. It says to build first when the
folder holds no Magic yet.

## NativeBindingsGenerator

The generator behind every native library Magic binds: a `dotnet` project the build runs for each library a project
declares, turning the library's C headers into the C# the project compiles. How it reads the headers and what each C
declaration becomes is in [its README](NativeBindingsGenerator/README.md).
