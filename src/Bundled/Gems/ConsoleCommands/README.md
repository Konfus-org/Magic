# ConsoleCommands

Commands typed in the terminal Magic was started from, or piped into it: provides `ICommands`, which any gem adds its
own commands to, each with a line saying what it does and the words it takes, which `help` shows. A thread of its own
reads the lines, and they run on the main thread, in Update, one after another.
A console of another kind, such as a window, hands its lines to `ICommands.Run`, which runs one at once. It has no
settings.

A line's first word names the command, and the rest are its words. Spaces part words, but not inside double quotes,
braces or brackets, so `"two words"` is one word and `{"a": [1, 2]}` one that keeps its JSON. Quotes around a whole
word are dropped.

## Built in

| Command | |
| --- | --- |
| `help` | Logs every command, a line each: its name and what it does. |
| `quit` | Stops the run. |
| `wait <frames>` | Holds the lines after it back for that many frames. |
| `screenshot` | The window's next image, as it is shown, saved as `Screenshots/<time>.png` beside Magic. The log says where. |

A script piped in runs as if typed, so a run can be told what to do without anyone at the terminal, and it goes on
across a restart of the engine (a `set` of a setting marked `[RequireRestart]`, say), the lines after it running in
the new run. The frames before a scene is drawn go by quickly, so wait well past them:

```bash
printf 'wait 1500\nwait 400\nscreenshot\n' | Magic --project samples --frames 2000
```

## Screenshots

The GPU gem draws the frame into an image of its own as well as the window, and reads it back once that frame is done,
holding the render thread for it once; a worker then writes it as a PNG. Without a GPU gem or a window (`--headless`),
there is nothing to take, and `screenshot` says so.
