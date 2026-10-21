# DebugTools

The debug UI's windows, drawn with `IDebugUI`'s widgets ([Core's README](../../../Core/README.md#the-debug-ui)), so it
takes the UI gem and is made after it. It provides nothing. It takes Core's `Settings`, the assets, the lifetime and
the events, and the commands gem, the input and the window when gems provide them: without input no key opens a
window, without a window Copy has no clipboard, and without commands the console only shows the log and the gem adds
no commands.

Three windows, each opened and closed by its key, or closed by the close button in its header: the console, on the key
left of 1 (`` ` `` on a US layout), the log and a line to run a command on; telemetry, on F3, what the engine measures
over the last second; and settings, on F4, every gem's settings, changed while running, the preset and what the frame
shows. F5 shows the next debug view and F6 the one before, round through the frame as it is, whether or not a window
is open. A right-click anywhere in a window opens its menu, whose Copy puts what it shows on the clipboard.

The frame rate and a frame's time show as the first line in the top left corner, in a Debug build or while the
settings say, averaged over a short while and worked out a few times a second, so they can be read. While a debug
view is shown, the line under it says which, and what the view shows, so a frame of black or a flat colour reads and
a screenshot says what it is of.

## Console

- The gem is a log sink, so the console has every line logged, the last many thousand of them: a line's time and its
  message, after its level when it is a warning or worse, a warning in orange and an error in red, as the reports on
  screen are. The lines from before the gem was made are there too, as the log keeps them for its first sink, so the
  console starts with the whole run.
- The filter above the lines shows only those that contain what is typed in it, in any case, and only the newest few
  hundred of them are drawn, so a frame stays flat however long the log; Copy gives every kept line, whatever the
  filter.
- A line typed and entered is logged after `>` and run as if it were typed at the terminal, through the commands gem,
  so `help` lists the commands there are and what each does. The field keeps the keyboard, for the next line.
- `clear`, the console's own command, empties its view; the log keeps its lines.

## Telemetry

- Laid out as the instruments are named, as an exporter shows them: a tab per entry of the settings' tabs, in their
  order, holding the meters under its prefix, and in it a header per meter and area, which a click folds away, and
  which is folded the first time it has very many rows, such as a GPU provider's time per pass, so it does not push
  the rest of its tab out of view: the
  meter past the prefix, so a gem's name in the gems' tab and nothing in Core's, then the area, the part of a name
  after its meter's. So Core's frame time is its last word under the frame's header, a GPU provider's upload size is
  its last word under the gem's upload header, and a .NET instrument, not named after its meter, loses its first part
  to be its last word under its area; a name with nothing after its area is its own area. Under each header is a
  table of rows, a column each for the name, the value, how many or how much a second, and the most, so the numbers
  line up and the slow row is found by eye; a column is resized by dragging its edge. Resting the mouse on a row shows
  what its instrument measures.
- An instrument measured over the last second without tags is one row; with tags, a row of its name, then one per
  set of tags, showing their values: the hook timing, then a gem and its hook. The rows keep the order the
  instruments were made in, and a series the order it was first measured in, so they stay put from one second to the
  next.
- A histogram's row is its mean, how many a second and its most, so the frame time's row is the frame time, the
  frame rate and the slowest frame. A counter's is its total over the second and its rate; a running total's, such
  as the garbage collector's, the total and how fast it rose; a gauge's its latest value alone. Seconds are shown as
  milliseconds, bytes as megabytes, and a count's unit not at all.
- It listens only while the window is open, so nothing is measured for it otherwise, and opening it shows its first
  numbers a second later. What a gem measures only while listened to starts then too, and can cost: a GPU provider
  that times each pass slows the frame while it does, so the frame time with the window open is not the frame time
  with it shut. Measurements are added up on the thread that records them, without allocating after a
  series' first; the rows are made once a second. Copy gives every tab as text, a row a line.

## Settings window

- **Preset**: the preset applied over the default one, as a choice of every preset in `Presets/`, by name; choosing
  one applies it through `Settings`, dropping what was changed while running.
- **View**: the frame as it is, or one of the debug views, the passes in `Passes/Debug/` by name, shown in place of
  the finished image (told on `Events`); what each shows is the renderer's to say, and its pass's description says
  it, as the choice's tooltip and the line on screen.
- **Filter**: while it has text, every setting whose name, key or description contains it, in any case, shows in
  place of the tabs, under a header per tab and sub-category, so a setting is found without knowing its gem. It has
  the keyboard as the window opens, so typing filters at once.
- A tab per category of settings ([Core's README](../../../Core/README.md#gem-settings)), Render first and Debug
  last, the rest in the order the gems were made; in it a header per sub-category, which a click folds away, unless
  the tab has only one; and under it a row per setting, whose gem and key the tooltip names:
  - labelled with its name in words, and its description, its doc comment, shown while the mouse rests on it, both
    as `Settings` lists them;
  - a checkbox for a flag, a slider for a number, kept to its `[Range]`, a choice for an enum, and a text field for
    anything else: a string's text, or the JSON of a list, a dictionary or a handle, set when Enter is pressed in it.
- An edit is set through `Settings`, so it reaches the gem at the next frame's sync point and lasts until another
  preset is applied. Nothing is saved: Copy gives the open tab, or what the filter finds, as `Gem.setting=value`
  lines, the form `--set` and `set` take.
- A setting marked `[RequireRestart]` is read only as its gem starts, so its tooltip says that a change starts the
  engine again at once, every setting as it is, the world saved and put back so the game goes on where it was.

## Commands

The gem adds to the commands gem's, for the console and the terminal alike, what the windows do: `get` logs a gem's
settings as `Gem.setting=value` lines, those read only at start too; `set` changes one while running, as the settings
window does, and warns why when the value does not fit; `preset` logs or applies a preset; `view` shows a debug view
by its pass's file name, or alone the frame as it is; and `clear` empties the console's view. `help` says what each
takes.

## Its own settings

Each is documented on `DebugToolsSettings` and shown in the settings window. The frame-rate line is on in a Debug
build; the tabs lay out the telemetry window, and changed while running make it list the instruments again under the
new tabs.
