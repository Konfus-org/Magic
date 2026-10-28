# Magic

Magic is a plugin-driven game engine written in C#. It is being rewritten by hand; [the plan](docs/PLAN.md) says
where it is going. Today it is the `Magic` program, its log and asserts, and the build rules.

## Requirements

- The .NET SDK that `global.json` names.
- PowerShell for the scripts in `tools/`: `pwsh`, or Windows PowerShell 5.1 on Windows.

## Building and running

```powershell
dotnet build src/Magic.slnx
dotnet run --project src/Launcher -- --project <path> --headless
```

Options after `--` go to Magic; they are declared in [`Cli.cs`](src/CommandLine/Cli.cs). With
`--headless`, each line typed into the terminal runs as the same options, with or without the leading `--`, and `help`
lists them. A typed `project <path>` or `headless false` restarts the run. The build output goes to
`Build/bin/<Configuration>/`.

- Exit codes: `0` clean, `1` bad arguments or a project that won't open, `2` at the first error logged under
  `--fail-on-error`.
- A crash writes `Logs/<time>.crash` next to Magic.

## Folders

```
src/Launcher/    the Magic program
src/Debugging/   the log, asserts and crashes
src/Extensions/  extension members every project can use
src/State/       small value types the other projects share
tools/           the scripts and the analyzers; nothing here ships
docs/            the plan, how to contribute and the code standards
Build/           build output, not in git
```

## Contributing

See [Contributing](docs/CONTRIBUTING.md), the [Code Standards](docs/CODE_STANDARDS.md) and [Tools](tools/README.md).
