# Debugging

## Failures

When an asset or a part of the rendering fails, the frame is still drawn. The failure shows in the picture as a
flashing colour with text, and the log gets one error for it.

### What a failure looks like

The colour tells the kind of thing that failed. The pattern tells how much of it failed:

- **Solid:** the whole thing failed. The colour replaces the surface.
- **Stripes:** one part failed. Diagonal stripes of the colour, each with a black edge, lie over the real surface.

| Kind | Colour | Pattern | Where it shows |
| --- | --- | --- | --- |
| Model | Teal | Solid | A cube in the place of the model |
| Material | Magenta | Solid | The mesh of the entity |
| Shader of a material | Purple | Solid | The mesh of the entity |
| Param of a material | Magenta | Stripes | The mesh of the entity, over its real surface |
| Texture | Amber | Stripes | The mesh of the entity, over its real surface |
| Sky | Coral | Solid | The pixels of the sky |
| Weather | Green | Solid | A band of the sky above the horizon |
| Post effect | Blue | Solid | A frame round the window, with a line along the bottom |
| Render pass, resource or shader of a pass | Red | Solid | A line of the banner at the top of the window |

- The text names the kind and what failed, as `Model failed Trees/Oak.fbx`. A line of the banner names what the
  frame lacks, as `Shadows are not traced`.
- A failed param takes the default of its shader field. A failed texture reads as no texture.
- The sky and the weather also show as lines of the banner.
- The doc of the renderer tells more about each one, and how a project replaces the shader of the banner
  ([Failures](RENDERING.md#failures)).

### What stays true

- The frame is still drawn. Only the part that failed is missing.
- The log gets one `Log.Error` for each failure, in the first frame it shows in. A failure that goes away and comes
  back logs again.
- A run with `--fail-on-error`, as `Validate.ps1` starts, exits with code `2` at that error.
- A failure that goes away leaves the picture in the next frame.
- A failed material, texture or model loads again when its file changes, and its failure leaves the picture. This is
  also true when no file had its id and a file with that id is added. A material shader that fails stays failed until
  Magic restarts. A file that worked and then fails keeps what loaded before, and the log says so.
- A file that is removed while Magic runs keeps what loaded from it in the picture, and the log says nothing. Its
  failure shows after the next restart.
- A failure reads nothing from disk. The renderer builds its failure shaders at the start, and the shader of the
  banner is in the assembly of the renderer.

To take a picture of a failure, use `Screenshot.ps1` with `-AllowErrors` and `-Window`
([Screenshot.ps1](TOOLS.md#screenshotps1)).

### Report a failure from a gem

A gem that records passes reports a failure in its `Render`:

1. Call `RenderFrame.Fail` with a `RenderFailure` kind, a short constant text for the picture, and the reason for
   the log.
2. Return from the step for this frame. Do not record the passes that need what failed.
3. Try again in the next frame. Call `Fail` again in each frame that still fails.

Do not stop the gem for the rest of the run, and do not log the failure yourself. The graph logs it once.

Core reports two failures itself, each as a line of the banner:

- A gem whose step takes an input that no listed gem makes.
- A gem that threw in `Render`. Core runs that step again after a pause of a few seconds.

## ImGuiDebugUI

Draws the debug UI with [Dear ImGui](https://github.com/ocornut/imgui). Provides `IDebugUI`.

The debug UI shows what gems and scripts report: lines at the left of the picture, and short text at positions in the
world. A line logged with `onScreen` also shows there for some time. Other gems draw their debug windows with its
widgets.

In the `.magic` file of a project, list it with the GPU provider, the shader compiler and the ECS. Magic makes a gem
that takes `IDebugUI` after this gem.

- The UI draws over the window image, so it shows over the world. It is not in screenshots. A headless run has no
  window, so it draws no UI.
- Lines and text that gems ask for always show.
- Lines logged with `onScreen` show while a debug window is open. The settings can make them show at all times. In a
  Debug build, they show at all times by default.
- The settings of the gem are in `ImGuiDebugUISettings`, in the `Debug` category. `query Debug` lists them.
- Without a gem that provides `IInput`, you cannot click or type in the UI. Without a gem that provides `IClipboard`,
  you cannot copy or paste.
- The UI draws nothing until its shaders compile.

## DebugTools

Draws debug lines and windows for the measurements of the meters and for the settings of the gems. It provides
nothing. It needs a gem that provides `IDebugUI`.

| Tool | Shows |
| --- | --- |
| Counter | The frame rate, the frame time, and the memory of the GPU and the process, in the top-left corner |
| Stats | Each instrument of the `Magic.*` meters and the `System.Runtime` meter of .NET |
| Settings | The settings of each gem |

- In a Debug build, the counter shows from the start. In other builds, the counter line shows only while a debug
  window is open.
- The counter line is the first line of the debug text. In a Debug build, a load adds two lines under it: what it
  loads, and, indented, what it does now.
- The gem collects the numbers at an interval that a setting gives, and only while the counter or the stats window
  shows.
- The settings of the gem, with the key of each tool, are in `DebugToolsSettings`, in the `Debug` category.
  `query Debug` lists them.
- Without a gem that provides input, no key works.

### Stats

The stats window has sections that fold:

- **CPU** shows the frame of the game thread and of the render thread. Under each, it shows the time of each step of
  Core and of the hooks of each gem, the slowest first.
- **GPU** shows what the GPU provider tells about the API and the GPU, its frame and its passes.
- **Memory** shows the memory of the process and the GPU, then the instruments of Core, the process and each gem.
- **Other** holds the other instruments, by meter.

A histogram shows its mean, its count each second and its maximum. A counter shows its increase and its increase each
second. A gauge shows its last value.

### Settings

- A change applies at once.
- A setting marked `(restarts)` applies when you click its button. This restarts the run.
- **Save** keeps the settings for this user and project.

## Reference views

A reference view is a fixed camera in a sample project, with hours and named points, where Magic's light is measured
against a render of the same scene in Blender Cycles. The hours and the points are in
`tools/Scripts/ReferenceViews.json`, under the view's name. The cabin of the MountainCabin sample holds every view. The
faults that these pictures must reject are in the [rendering skill](skills/rendering/SKILL.md).

The Blender add-on in `tools/Blender/magic_domain` builds the scene from the files that Magic loads: the domain's
prefabs at their chunk corners, their models, the materials and their textures, the planar texture coordinates that
Magic gives a mesh without them, the lights with their range, the glass, and the sky with the sun or the moon of an
hour. It also puts an empty at each entity that only stands somewhere, which holds that entity's name and its tags.
The two scenes are the same scene, so a difference between the two pictures is a fault in one of them. What the
add-on cannot copy is listed at the top of `tools/Blender/magic_domain/__init__.py`.

`tools/Reference/BuildScene.py` renders the views: it asks the add-on for the scene, poses the camera at the view,
sets the hour, sets Cycles and writes a linear HDR EXR of each job, with the mask that keeps the sky out of the
numbers. It needs no install of the add-on: it puts `tools/Blender` on Python's path and imports the package from
there.

`tools/Scripts/UpdateReferences.ps1` is the one script that runs Cycles. It renders the reference of each view and
hour and stores it in the repository ([Tools](TOOLS.md#updatereferencesps1)):

| Where | What |
| --- | --- |
| `tools/Reference/References/<view>-<hour>.exr` | The light of the reference, half floats, with its mask beside it |
| `tools/Reference/References/manifest.json` | The hash of what each reference was rendered from, and the pose it was rendered at |
| `tools/Reference/Previews/<view>-<hour>-cycles.jpg` | The reference as a photograph, which the docs show |

Run it by hand after a change to the scene: the materials, the models, the lights, a view's camera, the hours, or
the modules of the add-on that build the scene. A reference's hash holds the files of the project, `BuildScene.py`
and those modules, so a change to one of them marks the references stale. The add-on's menu code is not in the hash,
because it does not reach a render.

To look at a domain in Blender yourself, or to lay one out there, install the add-on. It gives Blender
File > Import > Magic (.magic), which the reference renders use, and File > Export > Magic (.magic), which writes a
domain back. How to build its zip and install it, what the import makes and what the export writes and skips, are in
[its README](../tools/Blender/README.md).

Each view's camera is posed by an entity of the domain's `globals.prefab` with only a `Transform` and the tag
`render-reference`, named `ReferenceView.<Name>`. Magic finds that entity by its tag, and the Blender scene gets the
pose that Magic found, so the two cameras stand in the same place. A tagged entity that no view names, and a view whose entity is not tagged, both
end a run with a message that names it.

A tool reads the scene from the engine, not from the asset files. The commands that print what the engine knows, and
how to get their JSON from a headless run, are in the Rendering part of
[PROJECT.md](skills/PROJECT.md#the-engines-own-commands).

These parts of the tools still hold a copy of a rule of the engine's, which the engine can move away from. Read the
engine's own code before you trust one of them, and replace the copy with a command where you can:

| The copy | In |
| --- | --- |
| The prefabs of a domain, their records and the places they put an entity | `tools/Blender/magic_domain/domain.py` and `project.py` |
| A material's maps and values, and the planar texture coordinates of a mesh without them | `tools/Blender/magic_domain/materials.py` and `meshes.py` |
| A lamp's reach from its intensity | `tools/Blender/magic_domain/lights.py` |
| The transmittance of the air | `tools/Blender/magic_domain/lights.py` |

To run every visual test:

1. Install Blender 5.3 or later. The tools look for it at the Steam path in the script. Give another path with
   `-Blender`. Then install the packages that the reference tools need into Blender's own Python:
   `"<Blender>/python/bin/python.exe" -m pip install -r tools/Reference/requirements.txt`.
2. Run `tools/Scripts/VisualTests.ps1`. Give `-View` and `-Hour` a list to run part of the set. Give `-Tier Quick`
   to leave out the turns, the strafe and the light change, which is about half the time of a run. It prints PASS
   or FAIL for each measurement against the limits in `tools/Scripts/VisualTests.json`.
3. Read the paths it prints. Open the report page, then look at each picture at full size.

Magic runs one time for each view and hour, and each run waits for a free run slot. Its window is pinned to the size
of `picture` in `tools/Scripts/VisualTests.json`, which is the size the references are rendered at, so that a run on
any display measures the same picture.

The tests render nothing. They read the stored reference of each view and hour, and a reference that is not there, or
that was rendered from a scene that has moved on, ends the run before Magic starts and names what to render again. To
compare with a stored reference that the scene has moved on from, give `-Stale`: the table of that view then says
STALE. Cycles' own settings are data: `BuildScene.py` takes them by name with `--settings`, or in a `settings` key of
a job.

To look at one view alone, run `tools/Scripts/VisualTests.ps1 -View cabin-wall -Hour 12 -Tier Quick`.
Without `-View` it runs every view, and without `-Hour` each of the view's hours.

Everything of a run is written to `Screenshots/Reference/<view>-<hour>` beside Magic: Magic's picture, the
reference's picture, the ratio map, the FLIP map, the split picture and the measurements. The report page of the
whole run, `report.html`, is written beside those folders. Open it first: it holds each view and hour's table and
its pictures, the failing rows first. Every picture but the ratio map and the FLIP map gets the exposure and the
grade.

How to read the split picture:

- Magic's light is on the left and the reference's on the right.
- Both halves get one exposure, taken from the reference's median, then the scene's look: the grade of
  `tools/Scripts/VisualTests.json`, then one AgX curve. The two halves get the same numbers, so they compare.
- The measurements and the ratio map stay the raw light, before the exposure and the grade.
- The preview of the same view and hour is `tools/Reference/Previews/<view>-<hour>-cycles.jpg`.

How to read the ratio map:

- It compares the light before the exposure, in nits, so the exposure does not change it.
- Grey is the same light. Red is brighter than the reference, blue is darker. Full red is 8 times or more, and full
  blue an eighth or less.
- A thin line along an edge is the jitter of Magic's picture, not a fault.

How to read the FLIP map:

- It is NVIDIA's FLIP: the difference that a person sees between the two pictures, at the comparison's exposure.
- Black is no difference. The brighter a pixel is, the more a person sees there.
- The `mean FLIP` row is the mean of this map.

The table gives each point's name, its place as a part of the picture's width and height, Magic's light over the
reference's, and the colour of each as red over green and blue over green.

How to read the verdict beside PASS or FAIL:

- The baseline in `tools/Reference/Baseline` holds each row's value from an earlier run. A run with no baseline
  prints no verdict.
- WORSE means that the row moved away from its limit by more than its tolerance, BETTER that it moved towards it,
  and SAME that it moved less than that. The tolerances are in `tools/Scripts/VisualTests.json`.
- The command ends with 0 when every row passes, 2 when rows fail but none is WORSE, and 1 when a row is WORSE or
  the run broke.
- `-UpdateBaseline` writes the baseline again from this run. Run it only after the user approved the run's
  pictures.
- `-Repeat <n>` runs each view and hour n times and prints the spread of each row. It judges no baseline.

A row whose limit key is not in the limits file prints "no limit yet" and fails nothing. A row whose limit the file
marks as a proposal prints "proposed": the tools set that limit, and only the user makes it final.

The tool holds the day at the hour with `set Day.Hour`, so each `DayCycle` keeps the sun at the hour and the moon
opposite it.

These cannot be the same in the two pictures, so a difference there can come from the tool:

- The flicker of the fire and the lantern. Cycles uses their set intensity.
- Magic's visible sun disc, which the air dims to a twentieth, and its shadow rays, which use a disc four times the
  sun's.
- The moon disc and the stars, which Magic does not draw.
- A light that asks for no shadow. The renderer casts its shadow anyway, and the tool does the same.

The window glass is not one of these. The reference's grey frosted pane is what Magic's own `Glass` material asks
for, and Magic's clear pane is the one that differs.

To add a view:

1. Add an entity named `ReferenceView.<Name>` with only a `Transform` and the tag `render-reference` to the
   domain's `globals.prefab`. A positive pitch turns the camera down.
2. Add the view to `ReferenceViews.json`: its name, its project, that entity's name, its hours and its named
   points. The vertical field of view is always 60 degrees.
3. Render its references: `tools/Scripts/UpdateReferences.ps1 -View <name>`.

A view's data can also hold:

| Key | What it does |
| --- | --- |
| `"strafe": {"meters": <m>, "seconds": <s>}` | The camera glides that far along its own right axis in that time, which uncovers what nearer things hid |
| `"maxNits": <number>` on a point | Marks the point a leak point: it gets a `leak at <point>` row, which is the light that Magic leaves there, judged by the `leakNits` limit. The number of the field is the mark alone. A view whose every point states one, such as the sealed room, is measured at its own pose alone |

To add a point, add its name and its world position to the view. The tool projects it through the view's camera on
each run, so the point stays on the same surface at any resolution. Keep a point off the glass and off the doorway. A
point that falls outside the picture at any of the three camera angles is an error, not a measurement.
To find the world position of a pixel, take a screenshot with the `Depth` debug view and
`DeferredRendering.DepthViewFar` set. Then run `tools/Reference/Compare.py` with the view, its pose, `--pick <x> <y>` and `--depth <screenshot>`.

## Measuring

Core and each gem publish their measurements on their own `System.Diagnostics.Metrics` meter.

- Core uses the `Magic.Core` meter. A gem uses `Magic.Gems.<Gem>`, with instruments named
  `magic.gems.<gem_snake_case>.*`.
- Time is in seconds. Memory is in bytes.
- Magic makes the meter of a gem with the gem, and disposes it with the gem.

To publish a measurement from a gem:

1. For a value of each frame, record a gauge each frame. Do not make an observable gauge.
2. To time work, use a histogram of seconds with its `Start` and `RecordSince`. They do nothing while nothing listens.
3. For a number that the OS keeps for the whole process, use an observable gauge. Magic reads it when it needs it.

Where a measurement shows in the stats window:

- Core times the hooks of each gem and its own steps. A gem needs nothing more to show under CPU.
- An instrument named `gpu.*` shows under GPU. The values of its tags are the rows of a fold of its own. The tags of
  the meter are above it.
- An instrument in bytes shows under its gem in Memory. An instrument named `memory.usage` is the total of the gem.
  `gpu.memory.usage` also shows in the counter as the memory of the GPU.
- `process.memory.usage` is the RAM in the counter and in the Memory header. The platform gem publishes it. Without a
  platform gem, they show the working set of .NET, which also counts the pages that the process shares.

The console command `query stats [category] --every <seconds>` of Core logs the instruments as text, each so many seconds. It works
with or without the debug tools gem. For example, `query stats gpu --every 30` logs the instruments with `gpu` in their name.
