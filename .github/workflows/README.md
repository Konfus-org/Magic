# Continuous integration

What GitHub runs on a change and what must hold for it to pass. The checks run the same script a developer runs,
`tools/Validate.ps1` ([Tools](../../tools/README.md)), so a change that passes locally passes here, with one
exception: the `Window` step and `Screenshot.ps1` need a display and a GPU, which no runner has, so those stay on
developer machines. Everything else, including the headless sample run, runs here.

## When the checks run

Every workflow runs on a pull request into `dev` or `main` and on a push to either, never on another branch: a
branch is validated when it asks to merge, and the merged result once more. A newer push to the same pull request
or branch cancels the run it supersedes. Every job has a time limit, so a stuck step ends the job instead of holding
a runner; the limits are generous for the jobs that may build the native libraries from source (below) and tight for
the rest.

The repository's rulesets, not a file here, are what make the checks required: `dev` and `main` should require every
Build job, Test, Docs and CodeQL to pass before a merge, so that Dependabot's pull requests and everyone else's are
held to the same bar. `main` alone publishes a release, after its own Release-configuration build, tests and headless
run passed on every OS.

## The native libraries

Every native library is built from source by the first build, and one of them is an LLVM, so a cold build takes an
hour or more while a warm one takes minutes. `Build/NativeLibs/` is therefore cached per OS and architecture, keyed on
the files that declare the libraries. The build's own markers make a restored cache a no-op, and a cache restored
under an older key still serves: the build sees which library's pin changed and rebuilds that one alone. CMake's
working folders are dropped before the save, as nothing reads them again, and the save runs whether the job passed or
not, so a cold native build is never lost to a managed error after it. Both are in the two composite actions beside
the workflows, which every building job starts and ends with.

A runner has no user to keep the machine usable for, so the native builds take every core (`MagicNativeCores`,
which the build otherwise caps at half) and use Ninja on Linux and macOS. The weekly scheduled Build run keeps
`main`'s caches from expiring, and a branch with no cache of its own falls back to `main`'s, so a cold build happens
once per OS per change to a library's pin. When a cold build nears its time limit, the fix is a larger runner, named
in the matrix, not a longer limit.

## What a runner needs

The .NET SDK `global.json` names, Python 3 with `ply`, CMake and a C++ toolchain, as a developer's machine does
([README](../../README.md#requirements)). Windows and macOS runners come with theirs; Linux installs SDL's build
dependencies. The setup action installs everything else.

## Releases

A push to `main` builds the `Release` configuration on every OS, runs the tests and the headless samples against it,
and packages Magic's output folder without what the build and the runs left in it: a zip on Windows, a tarball on
Linux and macOS, so the executable bit survives. Once all three passed, one GitHub release is created at that commit,
tagged `v<version>` with generated notes. The version is Core's `VersionPrefix` with the run number as the patch, so
every release is newer than the one before and nothing is bumped by hand; the prefix changes when the major or minor
version does.

## Dependabot

Weekly pull requests into `dev` update the NuGet packages and the actions, grouped so each ecosystem is one pull
request. The build fails the restore on any package advisory, so this is what turns an advisory into a fix the checks
then validate.
