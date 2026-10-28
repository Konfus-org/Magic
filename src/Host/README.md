# Magic.Host

The `Magic` program. It reads the command line, then runs the project headless or, later, in the GUI.

- The options are declared in `Program.cs`.
- Options given at launch are only read, so `--help` prints nothing yet. Their actions run when typed into the
  headless console.
- Headless, each line typed into the console is parsed as the same options and run: `--exit` quits.
- To debug from Visual Studio, set the arguments in `Properties/launchSettings.json`.
