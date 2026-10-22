# Samples

The sample project shows the engine's features. It is laid out like any game: `Samples.magic`, an `Assets/` folder,
and `Samples.csproj`, which compiles the scripts and components under `Assets/`
([the project file](../src/Core/README.md#the-project-file)). It uses the engine's default settings.

## Running

Run it from an IDE, or with `dotnet run --project samples` ([Building and running](../README.md#building-and-running),
[command line](../README.md#command-line)). The first run on a machine is slow: it builds every model's clusters and
compresses every texture. Later runs read them from [the cache](../src/Core/README.md#the-cache).

- Hold the right mouse button and move the mouse to look around. WASD moves, E and Q go up and down, and Shift goes
  four times as fast.
- On a gamepad, the sticks move and look, the triggers go up and down and the left shoulder goes faster.
- A left click, or the gamepad's south button, throws a ball.
- Each sample shows its own keys at the top of the screen.

## Opening a sample

Each sample is a domain. The project opens the hub first. To open another:

- fly through one of the hub's arches;
- press a number key: 0 for the hub, 1 to 6 for the samples;
- or type `open` and the domain's path in the console, such as `open Domains/Audio/Audio.domain`. The console is the
  terminal Magic runs in, or the in-game console on the `` ` `` key.

## Where things are

- Each domain is a folder under `Assets/Domains/` ([Domains](../src/Bundled/Resources/Domains/README.md)). Its
  `globals.prefab` holds what belongs to its whole world, such as the sky, the sun and the spawn.
- The player is `Assets/Prefabs/Player.prefab`, named in `Samples.magic`.
- The assets the domains share are in a folder per kind under `Assets/`, such as `Scripts/` and `Materials/`.

Every asset here is CC0. The textures and the cabin's furnishings are from Poly Haven, and the bell is from Kenney's
impact sounds. The rest was made for the samples.
