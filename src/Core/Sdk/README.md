# Building for Magic

A gem, or a project's own code, made outside Magic's repository builds for one Magic folder: the one the `MagicDir`
environment variable names, a build of the repository (`Build/bin/<Configuration>/`) or a release. Its csproj imports
the file here for its kind, `Gem.props` or `Project.props`, after its own properties; Magic's folder holds this folder
beside it, so the import is `$(MagicDir)/Sdk/<kind>.props`. The templates in the repository's `tools/Templates/` make
such a csproj, and its `tools/InstallTemplates.ps1` installs them and sets `MagicDir`
([Tools](../../../tools/README.md#templates)).

What must hold, since Magic loads code only from its own folder:

- **Built into Magic's folder.** Magic loads a gem from `Gems/<Name>/<Name>.dll` and a project's code from
  `Projects/<Name>/<Name>.dll`, the name being the gem's as a project lists it, or the project's as its `.magic` file
  names it. So the csproj is named for it, and the build puts the code there, with the NuGet packages it uses beside
  it. A build while Magic runs reloads a project's code; a gem is loaded once, as Magic starts.
- **Compiled against Magic, never copied.** The code runs in the Magic that loaded it, so a copy of Magic beside it
  would be a second one whose types are not the host's. The csproj targets the .NET that Magic runs on.
- **A gem's assets read where they are written.** A gem's own assets are its `Resources/` folder: a release build
  copies them beside the gem, and any other build records the folder in it, so an edit reaches a running engine at
  once. Its `Description` and `Authors` are shown as Magic starts it, and its settings' doc comments are their
  descriptions, so its documentation file is built.
- **The project's Run is Magic.** A project's Run, an IDE's or `dotnet run`'s, starts the Magic in `MagicDir` on the
  project, in the project's folder. Magic writes its logs in its own folder and its caches in the machine's, so the
  project's folder holds only what its author puts there.
- **A release build is the game.** `Project.targets`, which `Project.props` imports, makes a `Release` build copy the
  project's `.magic` file and `Assets/` into `Projects/<Name>/` beside its code, then bake its caches there: it runs
  Magic headless on that copy with `--bake`, which opens each domain in turn, its caches written to a scratch folder,
  then kept as the project's `Cache/`, which Magic reads before the machine's
  ([The cache](../README.md#the-cache)). The bake starts from nothing whenever the project, its code or the Magic it
  runs with changed, so the cache holds what this build's domains make and nothing an earlier one left. It needs a
  GPU, and a bake that cannot run, or logs an error, fails the build; `-p:MagicBake=false` leaves it out. What a
  script makes later that no domain shows is made on first use, as without a bake.

Nothing else is imposed: the repository's analyzers, banned symbols and folder rules are its own house rules, and its
own gems and samples are set up by its build properties rather than by these files, but for `Project.targets`, which
its samples import too, so a project is baked one way. What a gem is and can ask for,
and what a project file says, is in Core's README ([Gems](../README.md#gems),
[the project file](../README.md#the-project-file)).
