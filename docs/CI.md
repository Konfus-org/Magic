# Continuous integration

GitHub runs the checks on each pull request into `dev` or `main`, and on each push to these branches. You can also
start them by hand from the Actions tab. The workflows are in `.github/workflows/`.

## Checks

| Check | What it does |
| --- | --- |
| Build | Runs the Build and Headless steps of `tools/Scripts/Validate.ps1` in `Release`, on Windows, Linux and macOS ([Tools](TOOLS.md#validateps1)) |
| Test | Runs the Test step on Linux |
| Commits | Makes sure that the subject of each commit starts with `type: ` ([Commits](../AGENTS.md#commits)) |
| Docs | Makes sure that each link in the Markdown files resolves |
| CodeQL | Runs the CodeQL analysis of GitHub on the C# code |

The Screenshot step does not run in CI.

- Each check must pass before a merge into `dev` or `main`. Give each job a name that no other job has.
- The Commits check reads the commits of a pull request, or the commits of a push since the commit before it. After a
  force push, it reads the commits since `main`.
- A new push cancels the run that it replaces. A Build on `main` is not cancelled.
- On a push, the first OS whose build fails cancels the builds on the other OSes. On a pull request, all the builds run
  to the end.

## Headless runs

The headless run needs a Vulkan device. It opens the samples from the `Release` build.

- Linux uses Mesa's software driver (lavapipe) and the offscreen video driver of the platform library.
- Windows gets the Vulkan runtime and lavapipe from an action.
- macOS gets the Vulkan loader and MoltenVK from Homebrew.

## Native libraries

CI keeps the native libraries in a cache in `Build/NativeLibs/`, one for each OS and architecture. The key of the
cache comes from the projects of the gems and from `Directory.Build.targets`.

- A cache with an older key still works. The build rebuilds only the libraries that changed.
- A branch without its own cache uses the cache of `main`. A weekly Build run keeps that cache.
- CI saves the cache when the job passes or fails, but not when it is cancelled.
- The native builds use all the cores. If a cold build gets near its time limit, use a larger runner. Do not make the
  limit longer.

## Releases

Each Build job packages the output folder of Magic: a zip file on Windows, and a tarball on Linux and macOS. The run
keeps the package for a week.

When all the builds of a push to `main` pass, the packages become a GitHub release with the tag `v<version>`.

- The version is the `VersionPrefix` of Magic, with the run number as the patch. A project with its own version, such
  as a gem, keeps its version.
- The release notes list the commits since the last release, in groups by type, as `cliff.toml` sets. The notes of
  GitHub follow them.

## Dependabot

Each week, Dependabot opens one pull request into `dev` for the NuGet packages, and one for the actions. Their
subjects start with `build:` and `ci:`.
