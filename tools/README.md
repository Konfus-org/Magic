# Tools

What the build runs and what checks a change; nothing here ships. A tool is a `dotnet` project the build invokes,
such as the bindings generator, or one cross-platform PowerShell script that runs the same under `pwsh` on Linux and
macOS and under Windows PowerShell 5.1, so it avoids what 5.1 lacks (`&&`, `?:`, `??`). A script runs from any
current directory, finds the repository from its own folder, and reports with its exit code: 0 passed, anything
else failed, and the failing command's code when it has one.

## Validating a change

`Validate.ps1` runs what [AGENTS.md](../AGENTS.md) asks of every change, in order, and stops at the first failure:
builds the solution, where every warning is already an error; runs the tests; runs the samples headless under
`--fail-on-error`, which exits 2 when any error was logged; and runs them again with a window, which needs a
display and a GPU. It ends with one line per step and its exit code, then `PASSED` or which step failed. `-Only`
picks the steps when the rest already passed, `-Frames` how long each sample run lasts, `-Configuration` which
build, and `-Project` another project than the samples.

`Screenshot.ps1` is the visual check: it runs the project with a window, pipes `wait` and `screenshot` into the
console, and prints the PNG's path as its only output, so `$png = tools/Screenshot.ps1` keeps it. The scene is not
drawn in the first frames, so `-Wait` holds the screenshot back well past them, and `-Frames` must reach past that.
`-Commands` sends console lines before the screenshot, such as choosing a debug view, so what the image shows is
chosen rather than found. It does not build: run the build step first.
