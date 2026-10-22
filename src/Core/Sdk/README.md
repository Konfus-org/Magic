# Building for Magic

A gem or a project's code kept outside Magic's repository builds for a Magic folder: the one the `MagicDir`
environment variable names, such as a repository build (`Build/bin/<Configuration>/`) or a release. The easiest start
is a template: `tools/InstallTemplates.ps1` installs them and sets `MagicDir`
([Templates](../../../tools/README.md#templates)).

To set up a csproj by hand, name it after the gem or project, target the .NET version Magic runs on, and import the
file here for its kind after its own properties:

- A gem imports `$(MagicDir)/Sdk/Gem.props` and builds into `Gems/<Name>/` in Magic's folder. Its own assets go in
  its `Resources/` folder.
- A project's code imports `$(MagicDir)/Sdk/Project.props` and builds into `Projects/<Name>/`. The csproj goes next
  to the `.magic` file. Running it, from an IDE or with `dotnet run`, starts Magic on the project.

A `Release` build of a project copies its `.magic` file and `Assets/` into `Projects/<Name>/`, then bakes its caches
into a `Cache/` folder there by running Magic with `--bake` ([The cache](../README.md#the-cache)). Baking needs a
GPU. Pass `-p:MagicBake=false` to skip it.

What a gem is and what a project file holds are in Core's README ([Gems](../README.md#gems),
[the project file](../README.md#the-project-file)).
