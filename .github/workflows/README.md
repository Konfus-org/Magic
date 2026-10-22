# Continuous integration

What GitHub runs on a change and what must hold for it to pass. The Build and Test workflows run the same script a
developer runs, `tools/Validate.ps1` ([Tools](../../tools/README.md)), a step at a time: Build runs its Build and
Headless steps on Windows and Linux, Test its Build, Format and Test steps on Linux. Every build already fails on
any analyzer rule, so the format check adds what no build can check, such as a file's line endings, and it runs once, as
formatting is the same on every OS. The Screenshot step needs a display, which no runner has, so it stays on developer
machines. The Headless step still needs a GPU device,
since the samples render offscreen: Linux gets Mesa's software Vulkan driver
and runs the platform library's offscreen video driver, both from the setup action. The Release build needs it too, as
it bakes the samples' caches by running Magic on them where it built them; the Headless step then opens that built
project, so it runs on what ships and reads the cache the package carries. Two checks have no local
equivalent: Docs follows every link in the Markdown, to files, their headings and the web, so a moved file or a renamed
heading fails here instead of going quietly stale; CodeQL is GitHub's security analysis of the C#, which reads the
sources and the restored packages without compiling, so it needs no native libraries and runs in minutes.

Every build here is the `Release` configuration, the one that ships: a push to `main` publishes exactly what the checks
built and ran. `Validate.ps1` builds `Debug` unless told otherwise: the configuration a developer works in, which
checks integer overflow and is not optimised, where `Optimize` is optimised with symbols and `Release` optimised
without them. Beside two `#if DEBUG` blocks, all three compile the same code, so a change validated locally is checked
again here as what ships.

## When the checks run

Every workflow runs on a pull request into `dev` or `main` and on a push to either, so a branch is validated when it
asks to merge, and the merged result once more. Each can also be started by hand from the Actions tab, and Build runs
on a weekly schedule as well, which keeps the caches alive (below). A newer push to the same pull request or branch
cancels the run it supersedes; Build alone finishes on `main` regardless, where a release in progress is never cut
short. On a push to `dev` or `main`, Build fails fast: the first OS to fail cancels the other, since the push has
one verdict and a runner's hour is not spent confirming it. A pull request's builds run to the end, so its author
sees every OS's result at once. Every job has a time limit, so a stuck step ends the job instead of holding a runner; the limits are generous
for the jobs that may build the native libraries from source (below) and tight for the rest.

The repository's rulesets, not a file here, are what make the checks required: `dev` and `main` require the Build job
for each OS, Test, Docs and CodeQL to pass before a merge, so that Dependabot's pull requests and everyone else's are
held to the same bar. That is what makes a merge into `main` a release of checked code. GitHub knows a check by its
job's name alone, whatever workflow it is in, so every job here carries a name no other job has.

## macOS

macOS is planned but not yet supported: the renderer is moving to Vulkan, which reaches a Mac only through a
translation layer, so nothing is built for it. When macOS is supported, the matrix takes it back, and the rulesets
require its Build check again.

## The native libraries

The native libraries are fetched and built by the first build
([Native libraries](../../src/Bundled/Gems/README.md#native-libraries)); the shader compiler, a whole compiler
toolchain, comes prebuilt on Windows and Linux, so a cold build is short, while a warm one does nothing. The build puts the libraries in a folder per machine, which the
setup action pins to `Build/NativeLibs/` in the workspace, cached per OS and architecture and keyed on the gems'
projects, which declare the libraries, and the build that reads them. The libraries' ready markers make a restored
cache a no-op, and a cache restored under an older key still serves: the build sees which library's item changed and
builds that one alone. CMake's working folders are dropped before the save, as nothing reads them again (the build
keeps the library it took last when a working folder is gone), and the save runs whether the job passed or
failed, so a cold native build is never lost to a managed error after it. It never runs for a cancelled job: a key is
saved once and never overwritten, so a snapshot cut short would be restored and rebuilt from on every run, and never
completed, until the key next changes. Both are in the two composite actions beside
the workflows, which every building job starts and ends with.

A runner has no user to keep the machine usable for, so the native builds take every core (`MagicNativeCores`,
which the build otherwise caps at half). The weekly scheduled Build run keeps
`main`'s caches from expiring, and a branch with no cache of its own falls back to `main`'s, so a cold build happens
once per OS per change to a library's pin. When a cold build nears its time limit, the fix is a larger runner, named
in the matrix, not a longer limit.

## What a runner needs

What a developer's machine does ([requirements](../../README.md#requirements)). Windows runners come with the
toolchain; Linux installs the platform library's build dependencies, and the setup action installs the rest.

## Builds and releases

Each Build job builds on its OS, runs the headless samples against the result, and packages Magic's output folder
without what the build and the run left in it: a zip on Windows, a tarball on Linux, so the executable bit
survives. The package is kept with the run for a week, so a build from any pull request can be downloaded and tried.
On a push to `main`, once every build passed, one GitHub release is created at that commit from those same
packages, tagged `v<version>` with generated notes. Nothing is built twice.

The version is Core's `VersionPrefix` with the run number as the patch, so every build is newer than the one before
and nothing is bumped by hand; the prefix changes when the major or minor version does. The number is written into
the assemblies through MSBuild's `Version` property; a project with a version of its own, as every gem has, keeps it.

## Dependabot

Weekly pull requests into `dev` update the NuGet packages and the actions, grouped so each ecosystem is one pull
request. The build fails the restore on any package advisory, so this is what turns an advisory into a fix the checks
then validate.
