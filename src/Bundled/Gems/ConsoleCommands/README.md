# ConsoleCommands

Runs commands typed in the terminal Magic was started from, or piped into it. Provides `ICommands`, which other gems
add their own commands to. It has no settings.

Type `help` to list every command and what it does. A line is a command name followed by its words. Put a word with
spaces in double quotes. JSON in braces or brackets stays one word.

A script piped in runs as if typed:

```bash
printf 'wait loaded\nscreenshot\nquit\n' | Magic --project samples --seconds 600
```

- `wait loaded` holds the next lines until the world has loaded. `wait 100` holds them for 100 frames.
- `open` loads a domain by its asset path and holds the next lines until it is there.
- `screenshot` saves the window's next image as a PNG in `Screenshots/` next to Magic. Headless, there is nothing to
  take.
- `pause` and `resume` pause the world. `timescale 0.5` runs its time at half speed.
- `pause app` holds the frames. `step` runs one frame, or `step 10` ten, and holds the next lines until they have run.
  `resume app` lets the frames go on. While the app is paused, only lines from the terminal run.
- A piped script carries on across an engine restart.
