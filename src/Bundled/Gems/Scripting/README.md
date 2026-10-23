# Scripting

Runs the scripts attached to entities. Provides `IScripts`, and loads `.cs` assets as `Script`s
([Scripts](../../../Core/README.md#scripts)).

A `.cs` asset loads as the class named like the file. Prefabs attach scripts with their values. Code attaches one by
adding its handle to an entity's `Scripts` component, and detaches it by removing the handle.

- **Starting.** A script is made, given its values and started in the frame after it is attached.
- **Running.** Behaviours' `Update`, `FixedUpdate` and `LateUpdate` run after the ECS's systems, in the order the
  scripts were attached. A behaviour that throws is logged and ends.
- **Pausing.** While the world is paused, only behaviours whose class is `[Unpausable]`, or whose entity is tagged
  `unpausable`, run. Scripts still start.
- **Ending.** A script ends when its handle is removed or its entity is destroyed. It is disposed if it is
  `IDisposable`.
- **Reloading.** When the project's code is rebuilt, each script is replaced by a new copy of its class, on the same
  entity with the same values. Its own fields start over.
- **Restarts.** Across an engine restart, a script's values and fields are saved and put back. `Start()` isn't called
  again.
- **Pacing.** Behaviours far from the player's cameras get `Update` and `LateUpdate` less often, with a longer delta to
  match. `FixedUpdate` runs every step, however far.

Its settings are on `ScriptingSettings` and show in the settings window (F4). They set how far pacing starts and the
longest gap between updates. Set the longest gap to 1 frame to turn pacing off.

- A far behaviour can miss a key pressed on a frame it skips. An entity without a `Transform` is never paced.
- A script that a gem ships reloads only when the engine restarts.
- Fields holding services, delegates, tasks or disposables aren't saved across a restart.
