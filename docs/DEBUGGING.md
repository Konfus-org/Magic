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

1. Call `RenderGraph.Fail` with a `RenderFailure` kind, a short constant text for the picture, and the reason for
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
- The settings of the gem are in `ImGuiDebugUISettings`, in the `Debug` category. `get Debug` lists them.
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
- The gem collects the numbers at an interval that a setting gives, and only while the counter or the stats window
  shows.
- The settings of the gem, with the key of each tool, are in `DebugToolsSettings`, in the `Debug` category.
  `get Debug` lists them.
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

The console command `stats [category] <seconds>` of Core logs the instruments as text, each so many seconds. It works
with or without the debug tools gem. For example, `stats gpu 30` logs the instruments with `gpu` in their name.
