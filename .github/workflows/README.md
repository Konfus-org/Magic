# Continuous integration

GitHub runs the checks on every pull request into `dev` or `main`, on every push to either, and by hand from the
Actions tab. They run `tools/Scripts/Validate.ps1` in `Release` ([Tools](../../tools/README.md)): the Build and
Headless steps on Windows, Linux and macOS, and the Test step on Linux. They also check that every commit's subject
starts with `type(scope): ` ([Commits](../../AGENTS.md#commits)), check every link in the Markdown and run GitHub's
CodeQL analysis on the C#. The Screenshot step doesn't run here.

- The repository's rulesets require every check to pass before a merge into `dev` or `main`. Give every job a name
  no other job has.
- The Commits check reads the commits of a pull request, or of a push since the commit before it; after a force
  push, the commits since `main`.
- A newer push cancels the run it replaces, except a Build on `main`.
- On a push, the first OS whose build fails cancels the others. A pull request's builds always run to the end.
- The headless run needs a Vulkan device. Linux uses Mesa's software driver (lavapipe) and the platform library's
  offscreen video driver. Windows gets the Vulkan runtime and lavapipe from a maintained action. macOS gets the
  Vulkan loader and MoltenVK from Homebrew.
- The headless run opens the samples as the `Release` build baked them.

## Native libraries

The native libraries are cached per OS and architecture in `Build/NativeLibs/`, keyed on the gems' projects and
`Directory.Build.targets`. A cache under an older key still works: the build rebuilds only the libraries that changed.
A branch without its own cache uses `main`'s, which a weekly Build run keeps alive. The cache is saved whether the job
passed or failed, never when it was cancelled. Native builds use every core. If a cold build nears its time limit,
use a larger runner rather than a longer limit.

## Releases

Each Build job packages Magic's output folder, a zip on Windows and a tarball on Linux and macOS, and keeps it with
the run for a week. Once every build of a push to `main` passes, those packages become a GitHub release tagged
`v<version>`. The version is Core's `VersionPrefix` with the run number as the patch. A project with its own version,
such as a gem, keeps it. The release's notes list the commits since the previous release, grouped by type as
`cliff.toml` lays them out, with GitHub's own notes after them.

## Dependabot

Every week, Dependabot opens one pull request into `dev` for the NuGet packages and one for the actions. Their
subjects start with `build(deps)` and `ci(deps)`.
