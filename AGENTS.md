# AI Agent Guidelines

How agents work in this repository: the commands to run, how a task goes from proposal to merge, and the rules the
code and docs follow.

## Commands

The scripts run under `pwsh` and Windows PowerShell ([Tools](tools/README.md)).

- **Format:** `tools/Scripts/Format.ps1` formats the files a change touches, CSharpier for the layout. You don't need
  to run it yourself; the next command does.
- **Task complete:** `tools/Scripts/AgentTaskComplete.ps1 -Change <level>` formats, runs the steps of
  `tools/Scripts/Validate.ps1` the level needs and stamps the validated state. Run it when a task is done, and again
  after any later edit or rebase. A state already stamped at that level returns at once.
- **Validate:** `tools/Scripts/Validate.ps1` builds, runs the tests, loads every sample domain headless, then
  screenshots each one and prints the paths.
- **Screenshot:** `tools/Scripts/Screenshot.ps1` runs a project until its world loads and prints the screenshot's
  path. Use `-Domains`, `-Project` and `-Commands` to reach what the samples don't show.
- **Review:** `plannotator review` opens the change as a diff in the user's browser and returns when they comment or
  approve ([Finish](#finish)).

Validation means running what the change can affect. Pick the lowest level that covers it, or the higher one when
unsure:

| `-Change` | For                                                                               | Runs                                                                 |
| --------- | --------------------------------------------------------------------------------- | -------------------------------------------------------------------- |
| `Docs`    | Markdown, comments, docstrings                                                    | The build                                                            |
| `Code`    | Anything that runs differently but draws the same, refactors and renames included | The build, the tests, every sample domain headless                   |
| `Visual`  | Anything that can change what is drawn                                            | The build, the tests, a screenshot of every sample domain to look at |

Commit or merge only once `tools/Scripts/AgentTaskComplete.ps1` has stamped that exact state.

To run one step of Validate on its own:

```powershell
dotnet build src/Magic.slnx
dotnet test src/Magic.slnx
'wait loaded', 'open Domains/Audio/Audio.domain', 'quit' | Magic --project samples --seconds 1200 --fail-on-error --headless
tools/Scripts/Screenshot.ps1 -Domains 'Domains/Audio/Audio.domain'
```

`--fail-on-error` exits with `2` if anything was logged as an error.

- The build must have no errors, and no warnings but unused or unreachable code's (`Directory.Build.props`). Fix a
  warning at its cause. If a warning doesn't apply, say so in your summary instead of suppressing it.

## Before You Edit

| Document                                 | Read                                  |
| ---------------------------------------- | ------------------------------------- |
| [README](README.md)                      | Once per session                      |
| [Code Standards](docs/CODE_STANDARDS.md) | Before editing any `.cs` file         |
| [Contributing](docs/CONTRIBUTING.md)     | Before presenting a change for review |
| [Tests](tests/README.md)                 | Before running or touching tests      |
| [Tools](tools/README.md)                 | Before running a tool script          |

Before changing an area, read every `README.md` from that area's root folder down to the folder you are editing.

## Workflow

| Size     | What counts                                                                | Process                                                        |
| -------- | -------------------------------------------------------------------------- | -------------------------------------------------------------- |
| Small    | One file, about 20 lines or fewer, no public API change, no new dependency | In a worktree: change, validate, open the review               |
| Normal   | Anything larger                                                            | Proposal first, in plan mode; no edits until the user approves |
| Sweeping | Multi-file architectural or data-flow changes                              | Proposal with before/after snippets and a data-flow diagram    |

### Check the Request

- Check the request's premises against the code and docs. If it's the wrong direction, say so with evidence before
  doing anything.
- If a current .NET or C# feature does the job better than what the request names, say so.
- Before adding a component, dependency, file format or tool, look for an existing, well-maintained one and prefer
  it, even when the request names an approach.

### Proposal

For normal and sweeping changes, present:

1. The approach.
2. The alternatives you considered, and why the chosen one is the simplest.
3. For anything new, the existing libraries or tools you looked at.
4. The riskiest assumption, what you haven't verified, and what would change your recommendation.

### Implement

Make your worktree before the first edit ([Worktrees](#worktrees)), even if the task started as a question. State
assumptions and decisions a reviewer would question as you make them.

- **Fix the siblings.** Fix the same bug in its copies (overloads, the same pattern elsewhere, the same step in
  another gem) in the same change.
- **Never edit generated code.** Change what generates it: the generator, the headers list, the csproj item.
- **Leave it cleaner than you found it.**
  - **Where:** only in the members and files the task already changes, and what they directly call.
  - **What:** what these docs already forbid: a misleading name, a stale comment or one that restates its line, a
    block whose purpose isn't obvious and has no comment, a dead private or internal member, a `!`, a nested `if` a
    guard clause flattens, a duplicate, a docstring the change made wrong.
  - **How:** never a change in behaviour. The summary says which hunks are the fix and which are cleanup.
  - **When to stop:** a cleanup that grows past a few lines, reaches files the task doesn't touch, or needs a design
    decision becomes its own proposed task. Never touch another agent's in-flight work.

### Finish

1. Run `git rebase --autostash <merge target>`, then `tools/Scripts/AgentTaskComplete.ps1 -Change <level>`. If
   putting your work back conflicts, resolve it in your own changes and drop the stash. Fix a failing test at its
   cause; never loosen, skip or delete it.
2. For a `Visual` change, open and look at every screenshot it printed. If the change only shows somewhere the
   samples don't reach, screenshot that too with `tools/Scripts/Screenshot.ps1 -Project` or `-Commands`.
3. Summarize what changed, the level you validated at, what you assumed, what you couldn't verify and, for a
   `Visual` change, what the screenshots show. Keep the fix and the cleanup apart.
4. From the worktree, run `plannotator review --base <merge target>` in the background and wait for it. If it stops
   before the user decides, run it again; their comments are kept.
5. Address each comment, or say with evidence why not, validate again, and go back to step 3. If the review closes
   with neither comments nor approval, ask what the user wants.
6. Approval, in the review or typed as "approved", means commit and merge ([Worktrees](#worktrees)). Don't ask again.

### When Blocked

- A build or test failure outside your files: stop and report it. Don't fix other agents' work.
- An ambiguity that changes the design: ask. Otherwise pick the reasonable reading and say which.

## Design Rules

- **Simplest robust solution.** Prefer removing code to adding it. Match the surrounding style. No abstraction
  before a second real use. A private or internal member needs a caller; public API a gem or game could use stays
  without one.
- **Project conventions first, then .NET's** (the Framework Design Guidelines).
- **One responsibility each.** Split interfaces by what their user needs. Gems depend on Core's interfaces, never on
  each other.
- **Stateless where possible.** State that must exist follows [Code Standards](docs/CODE_STANDARDS.md#state).
- **Data-oriented, explicit data flow.** Only the inheritance, interfaces and indirection the problem needs.
- **Dependencies come from NuGet.** Never vendor or copy external code.
- **Tests:** don't write new tests unless asked. Run the existing tests that cover what you changed. When a test is
  asked for with a fix, write it first and see it fail ([Tests](tests/README.md)).

## Documentation Rules

- A doc says what a thing is and how to use it, in short plain sentences. No reasons, history or design decisions.
- No inventories: never list the files, gems, assets, settings or types in a folder.
- No copies of code: no member, option or default lists; point to the code, `--help` or the settings class.
- Outside configuration files, name another gem by its role ("the GPU provider"), never by its name.
- Docs change in the same change as the code.

## Git

### Worktrees

Every task runs in its own worktree. Never edit or commit in the main checkout.

- **Name:** `<type>/<feature>` in kebab-case, for example `fix/material-cache-leak`. Types: `feature`, `fix`,
  `refactor`, `docs`, `test`, `build`, `ci`. The worktree folder has the branch's name.
- **Start:** branch from the branch checked out in the main checkout. That branch is the merge target.
- **Finish,** once the user approves:
  1. Commit on the worktree branch.
  2. Rebase onto the merge target and run `tools/Scripts/AgentTaskComplete.ps1` again at the same level. If nothing
     landed it returns at once; if it validates again, look at a `Visual` change's new screenshots. Resolve conflicts
     in your own changes only; if one needs another agent's work changed, stop and ask.
  3. Fast-forward the merge target to your branch. Push nothing.
  4. `git worktree remove`, then `git branch -d` (never `-D`).
  5. Remove every other worktree and branch made for the task the same way, including the one the app made for the
     session. A branch cut from a remote branch goes with `-d` once its upstream is set to that remote branch.
  6. Delete the side sessions you started that are done ([Sessions](#sessions)).
- **Abandoned work:** if the user drops the task, leave the worktree and branch and say so.
- **Start of every session:** run `git worktree list` and `git branch --no-merged <merge target>`, and look at the
  repository's sessions. Remove worktrees and branches whose session is gone and whose commits are all merged (into
  the merge target or a remote branch), and delete sessions that are done. Report the rest. Never remove a worktree
  or branch with work found nowhere else.

### Sessions

A session is done when it isn't running, its work has merged or its question was answered, and nothing in it waits
on the user. Keep any session that was interrupted, never started, or waits on the user.

- Delete the side sessions you started once they're done. The user approves each delete.
- Before deleting a session, detach the side sessions it started that are still going, or leave it until they finish.
- After deleting a session, check `git worktree list` and remove its leftover worktree and branch as in step 5 of
  Finish.

### Commits

- The subject starts with `type(scope): `, which the repository's hook writes from the branch's type and the files
  staged ([Git hooks](.githooks/README.md)): `feature/` gives `feat`, every other branch type its own word, and a
  commit of Markdown alone `docs`; the scope names the areas the files are in, each gem by name, as in
  `fix(core, gem:Box3DPhysics,SteamAudio, samples): `. Write the subject alone, never an area prefix such as
  `Core, Gems:`. The `commit-msg` hook refuses a commit whose subject lacks one, and the Commits check a push.
- Commit only after the user approves the review.
- Never push.
- No co-author lines, and nothing in code, comments, commit messages or branch names that hints the work was
  generated.
