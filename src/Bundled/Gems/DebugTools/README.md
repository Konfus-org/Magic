# DebugTools

The debug UI's windows, drawn with `IDebugUI`'s widgets ([Core's README](../../../Core/README.md#the-debug-ui)), so it
takes the UI gem and is made after it. It provides nothing. It takes Core's `Settings`, and the commands gem, the input,
the window and the GPU when gems provide them: without input no key opens a window, without a window Copy has no
clipboard, without commands the console only shows the log and the gem adds no commands, and without a GPU there is no
frame to view.

| Window | Key | |
| --- | --- | --- |
| Console | the key left of 1 (`` ` `` on a US layout) | The log, and a line to run a command on. |
| Telemetry | F3 | What the engine measures, over the last second. |
| Settings | F4 | Every gem's settings, changed while running; the preset; what the frame shows. |

The frame rate and a frame's time show as the first line in the top left corner, in a Debug build or while
`frameRate` says, worked out twice a second.

A window's key opens and closes it, as does the close button in its header. A right-click anywhere in a window opens
its menu, whose Copy puts what it shows on the clipboard.

## Settings

In the project's `Presets/Default.preset`, by another preset, or `--set DebugTools.<key>=<value>`:

| Key | Default | |
| --- | --- | --- |
| `frameRate` | `true` in a Debug build | The frame rate and a frame's time, averaged over half a second, as the first line in the top left corner. |
| `meters` | `["Magic", "System.Runtime"]` | The meters the telemetry window shows, a tab each in this order, each with the meters under it: `"Magic"` is Core's and every gem's `Magic.<Gem>`, `"System.Runtime"` .NET's own: garbage collection, memory, the JIT and the thread pool. Read each time the window opens. |

## Console

- The gem is a log sink, so the console has every line logged from the moment the gem is made, the last 10,000 of
  them: a line's message, after its level when it is a warning or worse.
- A line typed and entered is logged after `>` and run as if it were typed at the terminal (`ICommands.Run`), so
  `help` lists the commands there are. The field keeps the keyboard, for the next line.
- `clear`, the console's own command, empties its view; the log keeps its lines.

## Telemetry

- Laid out as the instruments are named, as an exporter shows them: a tab per meter, in the order `meters` lists them,
  and in it a header per area, the part of a name after its first dot, which a click folds away. So
  `dotnet.gc.collections` is `collections` under `gc`, and a name of two parts is its own area. Resting the mouse on a
  line shows what its instrument measures.
- An instrument measured over the last second without tags is one line; with tags, a line of its name, then one per
  set of tags, showing their values: `hook.duration` under `gem`, then `Ecs Update`. The lines keep the order the
  instruments were made in, and a series the order it was first measured in, so they stay put from one second to the
  next.
- A histogram's line is how many a second, their mean and most, so `magic.frame.duration` is the frame rate, the frame
  time and the slowest frame. A counter's is its total over the second and its rate; a running total's, such as the
  garbage collector's, the total and how fast it rose; a gauge's its latest value. Seconds are shown as milliseconds,
  bytes as megabytes, and a count's unit, such as `{collection}`, not at all.
- It listens only while the window is open, so nothing is measured for it otherwise, and opening it shows its first
  numbers a second later. Measurements are added up on the thread that records them, without allocating after a
  series' first; the lines are made once a second.

## Settings window

- **Preset**: the preset applied over the default one, as a choice of every preset in `Presets/`, by name; choosing
  one applies it, dropping what was changed while running (`Settings.ApplyAsync`).
- **View**: the frame as it is, or one of its stages, shown in place of the finished image (`IGpu.DebugView`).
- A tab per gem with a setting it reads while running, in the order the gems were made, and a row per setting:
  - labelled with its name in words, and its doc comment shown while the mouse rests on it, read from the
    documentation file the build writes beside the gem, so nothing repeats the comment;
  - a checkbox for a flag, a slider for a number, kept to its `[Range]`, a choice for an enum, and a text field for
    anything else: a string's text, or the JSON of a list, a dictionary or a handle, set when Enter is pressed in it.
- An edit is set through `Settings.Set`, so it reaches the gem at the next frame's sync point and lasts until another
  preset is applied. Nothing is saved: Copy gives the open tab as `Gem.setting=value` lines, the form `--set` and `set`
  take.
- A setting marked `[Browsable(false)]` is read only as its gem starts, so it is not offered.

## Commands

Added to the commands gem's, as the console and the terminal run them:

| Command | |
| --- | --- |
| `get <Gem>` | Logs every setting of the gem as `Gem.setting=value` lines, those read only at start too. |
| `set <Gem.setting=value>` | Changes a setting while running, as the settings window does; why not, when it does not fit, is a warning. |
| `preset` | Logs the preset applied and every preset there is. |
| `preset <Name>` | Applies the preset of that name in `Presets/`. |
| `view <Stage>` | Shows a stage of the frame (`gbuffer`, `lighting`, `post`) in place of the finished image. |
| `view` | Shows the frame as it is again. |
| `clear` | Empties the console's view. |
