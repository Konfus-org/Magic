# Tools

What the build runs, what checks a change, and what a gem or project of one's own starts from; nothing here ships. A
tool is a `dotnet` project the build invokes, such as the bindings generator, or one cross-platform PowerShell script
that runs the same under `pwsh` on Linux and macOS and under Windows PowerShell 5.1, so it avoids what 5.1 lacks
(`&&`, `?:`, `??`). A script runs from any current directory, finds the repository from its own folder, and reports
with its exit code: 0 passed, else the failing command's code.

A script that runs Magic runs the one the build made, `Build/bin/<Configuration>/Magic` under the repository
(`Magic.exe` on Windows): the `Debug` one unless `-Configuration` says `Optimize` or `Release`. A script that finds
none says to build first.

## Format.ps1

`Format.ps1` formats the C# a change touches by the rules in `.editorconfig`, the ones the build and Validate's format
check hold the code to, so formatting is never a second rulebook. It takes the `.cs` files that differ from the last
commit, new ones included, and leaves every other file alone: it formats what is about to be reviewed, never someone
else's code; `-All` takes every file, for the change that adds a rule. `dotnet format`, which comes with the SDK, makes
five passes over them, each a pass of its own as each measures the code the one before it left: the ordering
analyzers' fixes, which move a type's members into the order the code standards give; the code-style fixes, which
order the usings, shorten names and apply every style rule that has one right answer, a one-statement method's `=>`
among them; the repository's own layout analyzers' fixes ([Analyzers](#analyzers)), which put a blank line before a
way out of a block and lay out an `if` and its `else`s, a body too long for its header's line below it; the
line-length analyzer's fix, which wraps a line of code longer than the limit at its argument lists, operators or
arrows, and the list analyzer's, which puts every item of a list that spans lines on a line of its own; and whitespace,
line endings and the final newline. A style fix that would delete code, invent it or rename it across the solution is
left out, as it needs a person, and the build still reports what it would have fixed; so are the other analyzers'
fixes, which change what code does.

Moving members changes nothing a type does but in one case the fix cannot see: static fields and properties with
initializers are set in the order they are written, so a static moved above one its initializer reads, directly or
through a method, reads it unset, and the type fails as it starts. What other statics read is therefore a field, which
the order puts first, with a property over it where other types read it; and a moved static is worth a look in review,
as the build passes it and only a run that uses the type shows it.

The wrap is mechanical. It breaks a line at the first place that fits, which is not always where a person would, and
never joins one back: a list inside an initializer can be indented a level too deep, a line that a later fix
shortened stays broken, and a fix that makes another possible (a body made `=>`, whose `new T()` can then be `new()`)
is left for the next run, which can break an initializer's brace onto a line of its own. One shape the list fix and the
whitespace pass each undo, so the build fails however often it runs: a call whose one argument is a `with` and its
initializer, begun beside the parenthesis, which goes on a line of its own instead. A wrap that reads badly is reshaped
by hand, and a line that fits is never wrapped again. It cannot wrap a string literal or a comment, and moving
one in can take it past the limit: a long string is split by hand, the next part starting with `+`, and a comment at a
word, as nothing checks either.

It reads the code as the last build left it, generated bindings included, and builds only the repository's own
analyzers, which `dotnet format` loads from their build. `AgentTaskComplete.ps1` runs it first, so an agent's change is
formatted before it is validated.

## Validate.ps1

`Validate.ps1` runs the checks [AGENTS.md](../AGENTS.md) asks of every change, in order, and stops at the first failure:
builds the solution, where every warning is already an error; checks the whole tree's formatting with `dotnet format`,
writing nothing, which fails on what `Format.ps1` would change, what no build can check among it, such as a file's line
endings; runs the tests against that build; runs the samples headless under `--fail-on-error`, which exits 2 when any
error was logged, until their world has loaded; and runs them with a window through `Screenshot.ps1`, which needs a
display and a GPU. Both runs wait for the world rather than for a number of frames, through the console's `wait loaded`,
so they prove the shaders compile and the domain opens, and a world that never loads fails rather than passing on the
loading screen. What bounds them is a time limit, Magic's `--seconds`, only a ceiling: a frame count would be none, as
a headless loading screen costs next to nothing to draw, so thousands of frames go by while a cold cache is made, and
how many depends on the machine. Each run then opens every other domain the project's assets hold, all
but its loading domain, in place of the world, one after another through the console's `open`, so a change that
breaks any sample fails, and the windowed run takes a screenshot of each; the time limit is per domain, as a cold
cache makes a large domain's models and textures again for minutes. It ends with one line per step and its exit
code, `PASSED` or which step failed, and the screenshots' paths, the entry point's first, each to be opened and looked
at: the run is the evidence that the change works, and the images are what show it. Its parameters, documented in the
script, pick the steps, the build and another project.

The build is incremental and never starts over: what is built is reused, and the native libraries are built once
per machine, in a folder every checkout and worktree shares
([Native libraries](../src/Bundled/Gems/README.md#native-libraries)), so a fresh worktree builds in minutes. It builds
with MSBuild's plain console logger rather than the terminal one, which shows a task's messages only after its
project ends, so which native library is building, and for how long, shows as it happens in a terminal too.

## Screenshot.ps1

`Screenshot.ps1` is the visual check: it runs the project with a window, pipes console lines in, and prints the PNGs'
paths as its only output, one a line, so `$png = tools/Screenshot.ps1` keeps them. It waits until the world has loaded,
however long a cold cache takes, then a while longer for what fades in to settle, takes the screenshot, then
opens each domain it is given in turn and takes one of each once it has settled, and quits; a world that never loads
within the time limit fails the run. The wait to settle is counted in frames, so every run's images are taken at the
same point. Console lines can be sent before the first screenshot, such as choosing a debug view, so what the image
shows is chosen rather than found. It does not build: run the build step first.

## AgentTaskComplete.ps1

What an agent runs on completing a task, and what makes "validation means the app ran" enforced rather than asked. It
formats the change with `Format.ps1` first, so what is validated, stamped and reviewed is the formatted code, then
runs `Validate.ps1` in full, and when every step passes it stamps the state it validated: the git tree of every file in
the checkout, new ones included, so the stamp names content, not a time, and any edit after the run, or a rebase onto
new work, leaves the new state unstamped. A checkout whose files changed during the run is not stamped. It ends with
the screenshots' paths, to open and look at.

The project's `.claude/settings.json` runs the same script with `-Hook` before an agent's `git commit` or `git merge`,
and it refuses the command, with the reason, unless the checkout's state, or for a merge the merged branch's last
commit, has a stamp. The stamps live in the repository's git folder, which every worktree shares, so the merge in the
main checkout finds the stamp its branch's worktree wrote. The hook reads its input on stdin and the checkout from where
the command runs, or the folder a `cd` or `git -C` in it names.

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

## Analyzers

`Analyzers` and `Analyzers.CodeFixes` are the repository's own analyzers and their fixes: rules no maintained analyzer
has, which every project in the solution runs as it builds, beside the packages `Directory.Packages.props` lists, so
they fail the build like any other rule and `Format.ps1` applies their fixes. A rule belongs here only when neither
those packages nor the SDK has it, as a maintained analyzer is one less to keep working; each rule's summary says what
it asks and why, and `.editorconfig` turns it on with the rest.

The analyzers reference the compiler alone, as the command-line compiler loads them where the IDE's workspace is
missing, and the fixes, which need the workspace, are an assembly of their own. Both build against a compiler no newer
than the SDK's and Visual Studio's, or neither could load them. A rule and its fix share how they read the code, so
what is reported is what is fixed. Visual Studio keeps the analyzers it loaded as it opened the solution, so a change
to one shows there once the solution is opened again.

## NativeBindingsGenerator

The generator behind the native libraries Magic binds that have not moved to Microsoft's ClangSharpPInvokeGenerator
([Gems](../src/Bundled/Gems/README.md#native-libraries)): a `dotnet` project the build runs for each such library a
project declares, turning the library's C headers into the C# the project compiles. How it reads the headers and what
each C declaration becomes is in [its README](NativeBindingsGenerator/README.md).
