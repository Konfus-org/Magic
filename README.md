# Magic

Magic is a plugin-driven game engine written in C#. It is being rewritten by hand; [the plan](docs/PLAN.md) says
where it is going. Today it is the `Magic` program, its log and asserts, and the build rules.

## Requirements

- The .NET SDK that `global.json` names.
- PowerShell for the scripts in `tools/`: `pwsh`, or Windows PowerShell 5.1 on Windows.

## Building and running

```powershell
dotnet build src/Magic.slnx
dotnet run --project src/Launcher -- open <path> --headless
dotnet run --project samples/HelloWorld
```

What follows `--` goes to Magic, whose commands are declared in [`Cli.cs`](src/CommandLine/Cli.cs); `--help` lists
them. With `--headless`, each line typed into the terminal runs the same commands, and an option may leave out its
leading `--`. A typed `open <path>` or `headless false` restarts the run. The build output goes to
`Build/bin/<Configuration>/`.

`Magic new <template> <name>` makes `<name>` with `dotnet new magic-<template>`, passing on any option Magic doesn't
know, such as `-o <folder>`. Install Magic's templates first with `tools/VS Templates/InstallTemplates.ps1`.

- Exit codes: `0` clean, `1` a bad command line, a command that failed or a project that won't open, `2` at the first
  error logged under `--fail-on-error`.
- A crash writes `Logs/<time>.crash` next to Magic.

## Folders

```
src/Launcher/    the Magic program
src/Host/        the app and the project
src/GemAPI/      the gem contract, and loading the gems a project lists
src/Debugging/   the log, asserts and crashes
src/Threading/   helpers for tasks and threads
src/Utils/       small value types and helpers the other projects share
samples/         the sample projects, which show and test Magic
tools/           the scripts and the analyzers; nothing here ships
docs/            the plan, how to contribute and the code standards
Build/           build output, not in git
```

## Contributing

See [Contributing](docs/CONTRIBUTING.md), the [Code Standards](docs/CODE_STANDARDS.md) and [Tools](tools/README.md).
