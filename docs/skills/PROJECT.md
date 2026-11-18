# Project settings for the skills

The skills in this folder work in any repository. This file gives the values that are different in each repository.
A skill names a value by its role, for example "the validate command". Find the role here.

To use the skills in another repository, copy this folder. Then change only this file.

## Rules

| Role | Here |
| --- | --- |
| Agent rules | [AGENTS.md](../../AGENTS.md) |
| Slice size limit | [Slices](../../AGENTS.md#slices) |
| Modes | [Modes](../../AGENTS.md#modes) |
| Decisions that need the user's yes | [Decisions](../../AGENTS.md#decisions) |
| How to implement | [Implement](../../AGENTS.md#implement) |
| When blocked | [When Blocked](../../AGENTS.md#when-blocked) |
| Simplest first | [Simplest First](../../AGENTS.md#simplest-first) |
| Documentation rules | [Documentation Rules](../../AGENTS.md#documentation-rules) |
| Branches and merges | [Branches and Merges](../../AGENTS.md#branches-and-merges). A hard rule: no skill overrides it. |
| Worktrees | [Worktrees](../../AGENTS.md#worktrees) |
| Commits | [Commits](../../AGENTS.md#commits) |
| Code standards | [Code Standards](../CODE_STANDARDS.md), for `.cs` files |
| Naming | [Naming](../CODE_STANDARDS.md#naming) |
| API shape | [API shape](../CODE_STANDARDS.md#api-shape) |
| Performance rules | Code Standards, Performance |
| Other standards | [Shader Standards](../SHADER_STANDARDS.md), for `.slang` files |

## Paths

| Role | Here |
| --- | --- |
| Worktree folder | `../Magic-worktrees/<branch>` |
| Logs | `Logs/` beside the Magic executable. Each run writes `Logs/<time>_<process id>.log`, and `query log` prints its path. A crash writes `Logs/<time>.crash`. |
| Reviews that git does not track | `Build/Reviews/` |
| Profiles and dumps | `Build/profiles/` |
| One area for a review | A gem, or a folder of Core |

## Validation levels

The levels, from the lowest to the highest, are `Docs`, `Code` and `Visual`
([Commands](../../AGENTS.md#commands)). The visual level is `Visual`. It prints the paths of the screenshots.

## Commands

| Role | Command |
| --- | --- |
| Validate and stamp | `tools/Scripts/AgentTaskComplete.ps1 -Change <level>` |
| Stamp only, for docs and comments | `tools/Scripts/AgentTaskComplete.ps1 -Change Docs` |
| The final merge | `git merge --squash <branch>`, then `git commit -m "<type>: <subject>"`, each in a command of its own, in the main checkout |
| Is a branch's work in its merge target | `git merge-tree --write-tree <merge target> <branch>` prints the same tree as `git rev-parse "<merge target>^{tree}"` |
| Validate one step | `tools/Scripts/Validate.ps1 -Only Build`, `Test`, `Headless` or `Screenshot` |
| Run the app | `tools/Scripts/Run.ps1 -Project <project> -Headless -Commands 'wait loaded', <commands>, 'exit'`. It exits `2` at the first error. Do not start the app in another way. |
| Take a screenshot | `tools/Scripts/Screenshot.ps1 -Project <project> -Commands <commands>` |
| Run one test project | `dotnet test --project tests/<Gem>.Tests/<Gem>.Tests.csproj` |
| Read a value while the app runs | The console command `query <setting>` |
| Take a profiling turn | `tools/Scripts/Profiling.ps1 -Minutes <n>`, at most 20 |
| Release a profiling turn | `tools/Scripts/Profiling.ps1 -Release` |
| The user's review | `plannotator review --base <merge target>`. `plannotator sessions --json` lists the URL and the worktree of each open review. |
| Review in a fresh context | In Claude Code, the `slice-reviewer` subagent |
| Files that every slice can touch | `src/Magic.slnx`, `Directory.Packages.props`, `Directory.Build.props`, `Directory.Build.targets`, `AGENTS.md`, `docs/` |

## Time limits

These add to the table in the [timeouts skill](timeouts/SKILL.md).

| Command | Limit |
| --- | --- |
| `dotnet build src/Magic.slnx` | 10 minutes |
| `tools/Scripts/Run.ps1` | Its `-Seconds` and one minute more, or 11 minutes |
| `tools/Scripts/Screenshot.ps1` | Two times its `-Seconds`, or 20 minutes |
| `tools/Scripts/AgentTaskComplete.ps1` or `tools/Scripts/Validate.ps1` | 60 minutes |
| `tools/Scripts/Profiling.ps1 -Minutes <n>` | 11 minutes |

A script that runs Magic or the tests waits while another worktree holds a profiling turn. Add the time that the turn has
left to the limit of that script.

## Profiling

The [profiling skill](profiling/SKILL.md) uses these values.

- **Configuration:** build and run `Optimize`. Do not compare Debug numbers: Debug has the validation layer and
  overflow checks. Use `Release` only when the user asks for it.
- **Start the app:** only with `tools/Scripts/Run.ps1`. The agent hook refuses another way.
- **GPU and VRAM:** do not use `-Headless`. A headless run can use a software device.
- **Process id:** `(Get-Process Magic | Where-Object Path -like '<worktree>*').Id`.

### The check run

Magic measures itself on its `Magic.*` meters and .NET's `System.Runtime` meter ([Debugging](../DEBUGGING.md#measuring)). The console
command `query stats <category> --every <seconds>` logs them.

```powershell
tools/Scripts/Run.ps1 -Configuration Optimize -Project <project> -Seconds 150 -Commands `
  'set Window.BackgroundFrameRate 0', 'set Window.MaxFrameRate 0', 'wait loaded', 'wait 10s', 'query stats all --every 20', 'wait 61s', 'exit'
```

- The agent's window does not have the focus, so `Window.BackgroundFrameRate` caps it. The first command removes the
  cap. Keep `Window.IsVSyncOn` off.
- On a laptop with two GPUs, make sure that the GPU tag names the discrete GPU.

### The numbers

Read `magic.core.*`, `magic.gems.<gem>.*` and `dotnet.*`.

| Area | Instruments | What they tell |
| --- | --- | --- |
| Frame | `frame.duration`, `frame.work.duration`, `render.frame.duration` | Work near the frame time: CPU-bound. Work far below it: waits or pacing. |
| CPU | `step.duration`, `gem.hook.duration` | The step, gem and hook that take the time. |
| GPU | `gpu.frame.duration`, `gpu.pass.duration` | GPU frame near the frame time: GPU-bound. Start with the slowest pass. `query stats gpu` lists the passes. |
| VRAM | `gpu.memory.usage`, `gpu.memory.allocated`, `gpu.memory.limit` | Use against the limit. The `source` tag says if the driver measured it. |
| RAM | `process.memory.usage`, `gc.last_collection.heap.size` | Growth that does not stop is a leak. Process growth without heap growth is native. |
| Allocations | `gc.heap.total_allocated`, `gc.collections`, `gc.pause.time` | A steady frame allocates nearly nothing. Gen 0 collections during play mean per-frame allocations. |

- Over `gpu.memory.limit`, the driver moves memory to system RAM, and the GPU time jumps.
- If `gpu.memory.allocated` grows each time the same world loads, a resource is not freed.

## Rendering

The [rendering skill](rendering/SKILL.md) and its [fault list](rendering/FAULTS.md) use these values.

- **The reference:** Blender Cycles, through the reference scene script.
- **The test scene:** the Cabin domain of `samples/MountainCabin/MountainCabin.magic`.

### Files

| Role | Here |
| --- | --- |
| The views file | `tools/Scripts/ReferenceViews.json`: each view's name, project, entity, hours and named world points |
| The limits file | `tools/Scripts/VisualTests.json`: the pass limits, the frame counts, the waits, Cycles' samples, and the values of the scene that a capture holds still |
| The previews folder | `tools/Reference/Previews`: `<view>-<hour>-cycles.jpg`, the reference of each view and hour as a photograph |
| The pictures of a run | `Screenshots/Reference/<view>-<hour>` beside Magic: Magic's picture, the reference's, the split picture, the ratio map, the FLIP map and the metrics |
| The report page of a run | `Screenshots/Reference/report.html` beside Magic |
| The baseline | `tools/Reference/Baseline`: each row's value from a run whose pictures the user approved |
| The references | `tools/Reference/References`: `<view>-<hour>.exr`, its mask and `manifest.json`, which git keeps with Git LFS |
| The script that renders them | `tools/Scripts/UpdateReferences.ps1`, the one script that runs Cycles |
| Blender | `C:/Program Files (x86)/Steam/steamapps/common/Blender/blender.exe` |

### Views

Each view is posed by an entity named `ReferenceView.<Name>` with the tag `render-reference` in the Cabin domain's
`globals.prefab`. To add a view or a point, follow [Reference views](../DEBUGGING.md#reference-views).

| View | Where |
| --- | --- |
| `cabin-wall` | Inside, at the window wall |
| `cabin-window-glow` | Outside, at a window |
| `cabin-room` | Inside, over the carpet, with the turning plate in the frame |
| `cabin-surfaces` | Inside, at the table that carries the test surfaces |
| `cabin-outside` | Outside, at the door |
| `cabin-trees` | In a gap at the clearing's edge, 40 m from the cabin, towards the forest |
| `leak-room` | In a sealed room 10 m behind the cabin. Each of its points states the light it may hold at most |

The hours are 8, 12, 16 and 22. The turns are at 6, 20, 120 and 360 degrees a second. The slow two show a trail
and a shadow that swims, and the fast two show what a mouse turn does. Each turn has the same limits, which are in
the limits file.

`cabin-room` and `cabin-surfaces` also glide 1.5 m along the camera's own right axis in 2 seconds. This move
uncovers what nearer things hid, which a turn does not do.

A point of a view can be a leak point, which `"maxNits"` marks. Its leak is then a row of its own, which the
`leakNits` limit judges. The sealed room states one on each of its points: its reference is black, so the rows that
compare the two pictures mean nothing there, and the view is measured at its own pose alone.

### The look

The scene leans to stylized realism. Each value is in the scene's data, which Magic and the Blender script both
read ([The look](rendering/SKILL.md#the-look)).

| Value | Where | In the cabin |
| --- | --- | --- |
| The sky's light | `Brightness` of the `Sky` in the domain's `globals.prefab` | 1.6, against 1 for the air itself |
| The fire | The cabin's prefab | 760 cd, a range of 16 m, the colour of 2700 K |
| The lantern | The cabin's prefab | 320 cd, a range of 12 m, the colour of 2600 K |
| The preview's grade | `grade` in the limits file | More saturation and contrast, warm highlights, cool shadows |

The sun and the moon take no part of this. Magic's own window shows no grade.

### Commands

| Role | Command |
| --- | --- |
| The visual tests | `tools/Scripts/VisualTests.ps1`, with a list in `-View` and in `-Hour` for part of the set, and `-Tier Quick` for the fast tier |
| The references | `tools/Scripts/UpdateReferences.ps1`, with `-View`, `-Hour`, `-Samples` and `-All`. It is the one script that runs Cycles |
| The judging and the report page of a run | `tools/Scripts/JudgeVisualTests.ps1 -Run <the folder of a run>`, which `VisualTests.ps1` calls at the end of a run |
| The comparison of one view | `tools/Scripts/VisualTests.ps1 -View <view> -Hour <hour> -Tier Quick` |
| Magic's HDR picture | The console command `screenshot --hdr`, through `tools/Scripts/Screenshot.ps1` |
| The reference scene | `tools/Reference/BuildScene.py`, through Blender, with `--view` and `--hour` |
| Every reference from one build of the scene | `tools/Reference/BuildScene.py --jobs <list of views and hours>` |
| Cycles' own settings, by name | `tools/Reference/BuildScene.py --settings <JSON>`, or a `settings` key of a job |
| The sun and the moon at an hour | The console commands `set Day.Hour <hour>`, then `query sky` |
| The light at an hour, in lux | `tools/Reference/BuildScene.py --light --hour <hour> --altitude <metres>`, which runs Magic for `query sky` |
| The comparison | `tools/Reference/Compare.py`, through Blender: the ratio map, the split picture, the preview and the metrics |
| The world point of a pixel | `tools/Reference/Compare.py --view <view> --pose <7 numbers> --pick <x> <y> --depth <screenshot>` |
| The settle and the edge noise | `tools/Reference/MeasureDenoise.py --moves <list> --out <folder>` |

Run a Blender script as `blender.exe -b --python <script> -- <arguments>`, always with a time limit.

### The engine's own commands

A tool or an agent asks Magic what Magic knows. The form of each command, and what its JSON holds, is in
[Supported Commands and Options](../CLI.md#supported-commands-and-options).

| Command | Gives |
| --- | --- |
| `query domain [--names <name>...] [--tags <tag>...]` | The open domain as the engine resolves it, as one JSON object |
| `query components` | Each component type that the run knows, with each field's name, type and default, and the same for a material |
| `query asset <path>`, `query asset --id <id>` | An asset's id, path and type |
| `new handle <file>` | An id for a file that has none |
| `query sky` | Each directional light with its rotation, colour and lux, and each factor of the sky's light. The sky gem adds it |

A gem adds a subject of `query` or a command of its own through `ConsoleCommands` ([Gems](../GEMS.md)).

Each `query` takes `--out <file>`. Get the JSON from a headless run:

```powershell
tools/Scripts/Run.ps1 -Headless -Project samples/MountainCabin/MountainCabin.magic -Commands 'wait loaded', 'query domain --tags render-reference --out views.json', 'exit'
```

`query` and `set` reach one entity by its tag or by its id, never by its name, and a tag is case sensitive.

### Time limits

| Command | Limit |
| --- | --- |
| One Cycles render of this scene | 8 to 14 minutes |
| `tools/Scripts/JudgeVisualTests.ps1` on the folder of a run | 2 minutes |
| `tools/Scripts/VisualTests.ps1 -Tier Quick` for one view and hour | 30 minutes |
| `tools/Scripts/VisualTests.ps1` for one view and hour | 90 minutes |
| `tools/Scripts/VisualTests.ps1` for every view and hour | 7 hours |

A command that runs Magic waits for a free run slot first, and its limit must hold that wait.

### Debug views and switches

`DeferredRendering.RenderView` shows one part of a frame: `Final`, `Albedo`, `Normal`, `Depth`, `Roughness`, `Metalness`,
`Occlusion`, `Velocity`, `IndirectDiffuse` and `Reflections`. `DeferredRendering.DepthViewFar` sets the distance at
which the `Depth` view turns black. A setting's key starts with the category of its own member, or with the category of its
settings class when the member names none.

These values turn off one term of the light, to measure each one alone. Set each with `set <value> false`. A
`GlobalIllumination` value is state of the run and is not saved:

| Value | What it turns off |
| --- | --- |
| `Rendering.IsTracedShadowsOn` | The sun's traced shadows |
| `Rendering.IsIndirectLightOn` | The light that bounces |
| `Rendering.IsReflectionsOn` | The traced reflections |
| `GlobalIllumination.IsHitProbeLightOn` | The probes' light at a diffuse ray's hit |
| `GlobalIllumination.IsDiffuseSkyOn` | The sky on a diffuse ray that reaches nothing |
| `GlobalIllumination.IsShadowFillOn` | The fill that a shadowed surface gets |
| `GlobalIllumination.IsPixelProbeLightOn` | The probes' light where the traced light does not cover a pixel |
