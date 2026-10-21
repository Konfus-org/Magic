# ImGuiDebugUI

The debug UI, drawn with Dear ImGui: provides `IDebugUI` ([Core's README](../../../Core/README.md#the-debug-ui)). It
draws what gems and scripts report, lines down the left of the window and text in the world, and the windows gems draw
with its widgets. It takes the assets, the window, the threads, the lifetime and the world, and the input when there is
one: without input nothing can be clicked or typed, and the world, which needs a gem that provides the ECS, is where
the cameras to put text in the world by are. The GPU and the shader compiler are taken by its pipeline script, not by
the gem, so the UI is drawn only where a renderer runs the script.

## Settings

Each setting is documented on `ImGuiDebugUISettings` and shown in the settings window. Each is read whenever it is
used, so all change while it runs. ImGui lays out in window pixels, so the font size says how big the whole UI is, and
a font whose `.meta` size is near it is the sharpest, as the glyphs are stretched from the size the asset was drawn at.

## Frames

ImGui's frame is the main thread's, and drawing it the render thread's:

1. **Update** hands ImGui the frame's input and starts its frame. A gem that takes `IDebugUI` is made after this one, so
   its Update runs after, and the widgets it calls there go into this frame. A widget called outside it throws.
2. **LateUpdate** ends any window left open, with an error the first time, and draws what is reported, into ImGui's
   background draw list, so the windows stay readable over the reports.
3. **Sync**, while both threads hold still, ends ImGui's frame and copies what it drew: vertices, indices, the draws
   into them, and its textures as they are. The copy is small, so it is made there rather than kept twice over.
4. **Render**, a frame behind, is the renderer's: the gem ships a pipeline script among its resources, run by a pass
   of the overlay stage in its overlay pipeline, which the renderer runs once for the window after every view in it
   is composed ([Core's README](../../../Core/README.md#the-gpu)), so the UI is on top of the scene whatever order
   the project lists its gems in. The script takes in the textures, uploads the vertices and indices, and records the
   draws into the window's image. Its pipeline is made for the window's format once its overlay shaders are compiled;
   until then, and for good when they do not compile, which is an error, nothing is drawn. The renderer makes the
   script anew with each listing of the frame it builds, so the UI is away while the first compiles.

ImGui's colours, the engine's look among them, are sRGB code as an 8-bit window's image takes them; a `Color` a gem
gives is linear, and is encoded to sRGB first. The window's image may be of another format, as the GPU provider's
composition decides, and the overlay's shader decodes the colours and encodes them for whichever it is, as the
renderer's last pass does for the scene. The GPU blends in the image's space, so a translucent window blends in sRGB
code over an 8-bit image, as ImGui means it to, and in linear over a linear or scRGB one, where it comes out a touch
lighter. Copy and paste go through the window's clipboard.

## Reports

- A report is kept, from any thread, until it has shown: text at a place in the world for the frame it is reported in,
  a line or a warning or error for its seconds after it was last reported. The same text at the same place again keeps
  it up rather than adding another, and a line reported again shows how often.
- An info, a warning or an error is logged too: an info every time, a warning or error when it first shows, not
  again while it stays up. Warnings are orange and errors red, between `!!!`s.
- What is reported shows, and the UI says it is enabled, while the window has an image: always in a Debug build, and
  in any other only while a window drawn with the widgets is open. Reports made meanwhile are kept as ever, and
  logged.
- Lines go down the left of the window, those reported every frame first, then the rest oldest first, as many of the
  last as fit, each on a dark plate. Text in the world is centred on its place on a dark plate, in every view of a
  camera that draws into the window, has it in front, and is within the distance the settings set: projected as the
  renderer frames that camera's part of the window. Both are drawn under the windows, so a window stays readable over
  them.
- A report's tooltip shows while the mouse rests on its text and on no window.

## Fonts

The font is the `Font` asset the settings name, loaded on a worker and made ImGui's between two frames; until then, or
when it does not load, which is a warning, ImGui writes in its own. ImGui asks for each character as it first draws it
at a size and gets the asset's, scaled from the size the asset was drawn at.

## Natives

Dear ImGui is C++ and ships no build, so the gem binds the C API that dear_bindings, the Dear ImGui team's binding
generator, makes from its public and internal headers, as it publishes it for each ImGui release; that and ImGui's own
sources, from its repository at the same release, are compiled into one library with wide characters and without
the obsolete API ([Declaring a library](../README.md#declaring-a-library)). The two pins move together: the C API is
generated for one ImGui release and compiled with that release's sources. Dear ImGui's
constructors are not in the C API, so a font configuration is set up field by field as ImGui's own constructor would.
The gem hands ImGui its text as the C strings it reads: every label and line goes into a per-frame arena of unmanaged
memory, good until the next frame starts, and a literal is read straight from the assembly's data.
