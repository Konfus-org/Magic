# Agents

How an AI agent works in this repository. What the engine is, how to build it and how its code is written are in
the documents below; read the ones your task touches before changing anything.

## Read first

| Document | For |
| --- | --- |
| [README.md](README.md) | What Magic and Wizard are, building, running, how the engine fits together, folder layout |
| [CODESTANDARDS.md](CODESTANDARDS.md) | Every rule for C#, performance, cross-platform code; the build enforces many of them |
| [CONTRIBUTING.md](CONTRIBUTING.md) | How contributions are reviewed |
| [Magic/Core/Resources/README.md](Magic/Core/Resources/README.md) | Assets: ids, `.meta`, formats, lifetime, hot reload, the default assets; each part's Resources folder has a README per kind |
| [Shaders README](Magic/Gems/Rendering/DeferredRenderer/Resources/Shaders/README.md) | How the renderer and its shaders work, and the rules that keep shaders correct |
| [Samples/README.md](Samples/README.md) | The samples, the console (`portal`, `summon`, `set`), domains and chunks |
| [Tools/VSTemplates/README.md](Tools/VSTemplates/README.md) | Writing a gem or a project from the templates |
| [Tools/Wizard/TUTORIAL.md](Tools/Wizard/TUTORIAL.md) | The Wizard editor |
| [TESTING.md](TESTING.md) | Test conventions, for when tests are asked for |

## How to work

- **Keep the change as small as it can be, and simple.** No speculative abstractions; follow the code around it.
- **Think out loud** while you work. At the end, justify the change and explain it in depth, with a preview of the
  `git diff`.
- **Tests are not required.** Do not write tests unless they are asked for; do run the existing ones when you change
  what they cover.
- **Sweeping changes get a plan first**: before/after code and a diagram of the data flow, approved before any edit.
- **Third-party libraries come from NuGet**; nothing is vendored.
- **Before Implementing** Please think step by step about whether there exists a less over-engineered and yet simpler, more elegant, and more robust solution to the problem that accords with KISS and DRY principles. Present it to me with your degree of confidence from 1 to 10 and its rationale, but do not modify code yet. 

## Git

- Work on the branch the main checkout has active (`dev`). Do not create branches or worktrees.
- Commit in small increments with short, precise messages. **Never push.**
- Never add a co-author line, and nothing in a commit may say or hint that an AI wrote it.
- Other agents may be editing the same checkout at the same time. Before committing, check `git status` and stage
  only your own changes (whole files, or hunks); leave anyone else's work uncommitted.

## Checking your work

- Build `Magic-Wizard.slnx`; warnings that matter are errors, so a clean build is the first check.
- Run the host on a sample with `--fail-on-error` (exit code 2 when anything logged an error) and, for anything
  visual, `--screenshots 1 --screenshot-delay <frame>`. `Magic --help` lists the options.
- The samples' cameras orbit with time, so two runs never make the same screenshot: compare scenes whose cameras
  stand still. GracefulFailures logs errors on purpose.
- Engine caches (`Cache/Shaders`, `Cache/Lods`) sit next to `Magic.exe` and are shared by every project run from
  that build: a run of another project can evict LODs and make the next run regenerate them.
