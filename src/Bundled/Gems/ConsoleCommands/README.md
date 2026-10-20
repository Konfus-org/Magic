# ConsoleCommands

Commands typed in the terminal Magic was started from, or piped into it: provides `ICommands`, which any gem adds its
own commands to, each with a line saying what it does and the words it takes, which `help` shows along with the
commands built in here. A thread of its own reads the lines, and they run on the main thread, in Update, one after
another. A console of another kind, such as a window, hands its lines to `ICommands.Run`, which runs one at once. It
has no settings.

A line's first word names the command, and the rest are its words. Spaces part words, but not inside double quotes,
braces or brackets, so `"two words"` is one word and `{"a": [1, 2]}` one that keeps its JSON. Quotes around a whole
word are dropped.

## Piped input

A script piped in runs as if typed, so a run can be told what to do without anyone at the terminal. The lines are the
process's rather than a run's, so a script goes on across a restart of the engine (a `set` of a setting marked
`[RequireRestart]`, say), the lines after the restart running in the new run, and a `wait` counts on across it, the
frames of the restart itself not counted. The frames before a scene is drawn go by quickly, so wait well past them:

```bash
printf 'wait 1500\nwait 400\nscreenshot\n' | Magic --project samples --frames 2000
```

`screenshot` asks the GPU provider for the window's presented image ([Core's README](../../../Core/README.md#the-gpu))
and a worker writes it as a PNG under `Screenshots/` beside Magic; the log says where. Without a GPU provider or a
window (`--headless`), there is nothing to take, and `screenshot` says so.
