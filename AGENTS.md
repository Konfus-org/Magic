# AI Agent Guidelines

How agents work in this repository: the commands to run, how a task goes from proposal to merge, and the rules
the code and docs follow. Most rules give their reason. When a case isn't covered, follow the reason.

## Commands

The tools run under `pwsh` and Windows PowerShell. [Tools](tools/README.md) covers them in full, including where
the `Magic` executable is built.

- **Validate:** `tools/Validate.ps1` builds `Magic.slnx`, runs the tests, and runs the samples headless and with a
  window. It reports each step's exit code. Every step must pass before work is done.
- **Screenshot:** `tools/Screenshot.ps1` runs the samples with a window and prints the screenshot's path.

What Validate runs, for when you need one step on its own:

```powershell
dotnet build Magic.slnx
dotnet test Magic.slnx
Magic --project samples --frames 120 --fail-on-error
Magic --project samples --frames 120 --fail-on-error --headless
```

`--fail-on-error` returns exit code `2` if anything was logged as an error during the run.

The build must finish with no warnings or errors. Fix a warning at its cause. If you believe a warning does not
apply, say so in your summary rather than suppressing it.

## Before You Edit

Read the document that covers what you are about to change:

| Document | Read | Purpose & Scope |
| --- | --- | --- |
| [Start Here](README.md) | Once per session | High-level overview of Magic and Wizard, build/run commands, engine architecture, and directory layout. |
| [Code Standards](CODESTANDARDS.md) | Before editing any `.cs` file | C# coding standards, performance rules, and cross-platform guidelines enforced by the build system. |
| [Contributing](CONTRIBUTING.md) | Before presenting a change for review | Code review processes and acceptance criteria. |
| [Tests](tests/README.md) | Before running or touching tests | Test suites, conventions, and requirements for requested test coverage. |
| [Tools](tools/README.md) | Before running a tool script | What `tools/Validate.ps1` and `tools/Screenshot.ps1` run, and where the `Magic` executable is built. |

Most gems, asset root folders and other large systems have their own `README.md` explaining how that area works
and what must hold for it to work. Before changing anything in an area, read every `README.md` on the path from
that area's root folder down to the folder you are editing. If you change how the area
works, update that README in the same change.

## Workflow

Scale the process to the size of the change.

| Size | What counts | Process |
| --- | --- | --- |
| Small | One file, roughly 20 lines or fewer, no public API change, no new dependency | Make the change, validate, show the diff |
| Normal | Anything larger | Proposal first, in plan mode; no edits until the user approves |
| Sweeping | Multi-file architectural or data-flow changes | Proposal with before/after snippets and a data-flow diagram |

### Check the Request

A request is a direction to check, not a conclusion to repeat. Verify its premises against the code, the docs and
what you can establish, and say plainly when it is not the right direction, with the evidence, before doing
anything. Agreement is only worth something when it could have been disagreement.

When the change adds a new component, dependency, file format or tool, first look for an existing, well
maintained solution, and prefer it to writing a custom one. This applies even when the request names a specific
approach: if that approach reinvents something that already exists, raise it.

### Proposal

For normal and sweeping changes, present:

1. The approach and why.
2. The alternatives you considered, and why the chosen one is the simplest and most maintainable.
3. For anything new, the existing libraries or tools you looked at and why they do or do not fit.
4. The riskiest assumption, what you have not verified, and what would change your recommendation.

### Implement

Work in your own worktree (see [Worktrees](#worktrees)). State assumptions and any decision a reviewer would want
to question as you make it.

### Finish

1. Run `tools/Validate.ps1`. A failing test is fixed at its cause, never loosened, skipped or deleted.
2. For any visual or rendering change, run `tools/Screenshot.ps1` and look at the image.
3. Summarize what changed and why, what you assumed, and anything you could not verify. Include the full diff;
   inside your worktree, `git diff` shows only your changes.
4. Wait for the user to reply "approved". Address feedback and repeat until they do.
5. As soon as the user approves, ask whether to commit and merge now; don't wait for them to bring it up. On a
   yes, follow [Worktrees](#worktrees). On a no, leave the worktree and branch in place and say that they are
   still open.

### When Blocked

- If a build or test failure is outside the files you changed, stop and report it. Do not fix other agents' work.
- If an ambiguity would change the design, ask. If it would not, pick the reasonable reading and say which one
  you took.

## Design Rules

- **Simplest robust solution.** Prefer removing or simplifying code over adding it. Match the surrounding style
  and patterns. Add no abstraction until a second real use needs it.
- **Stateless where possible.** State that must exist follows [Code Standards](CODESTANDARDS.md#state).
- **Data-oriented, explicit data flow.** Keep inheritance, interfaces and indirection to what the problem needs.
- **Dependencies come from NuGet.** Do not vendor or copy external code, so every dependency stays versioned and
  updatable in one place.
- **Tests:** do not write new tests unless the user asks. Always run the existing tests that cover code you
  changed.

## Documentation Rules

- **Docs change with the code.** Update comments, docstrings and `.md` files in the same change, so they stay
  accurate.
- **Explain, never inventory.** Docs say how a thing works and what must hold for it to work, never what is in
  which folder, file or namespace. No lists or tables of the files, assets, ids, types or packages there are, and
  no folder trees beyond where each kind of thing goes. For example, the Materials README explains how materials
  work, not which materials exist. The folder already shows what is in it, and a list is one more thing to keep in
  step that quietly goes out of date. Name a specific file only as the example that teaches a rule, or where the
  behaviour being explained depends on that path.
- **Gems by role, never by name.** Gems can be swapped at any time, so comments, summaries, docs and user-facing
  messages describe another gem only by what it does ("a platform gem", "the GPU provider", "a shader compiler"),
  never by its name or the library it wraps. A gem names itself only inside its own folder. Configuration that
  must list gems (project files, `Magic.slnx`, `.editorconfig` sections) is exempt.

## Git

### Worktrees

Each task runs in its own worktree, so concurrent agents never see each other's uncommitted changes. A worktree
lives only as long as its task.

- **Name:** `<type>/<feature>`, kebab-case, describing the change in a few words, for example
  `fix/material-cache-leak` or `feature/shader-hot-reload`. Types: `feature`, `fix`, `refactor`, `docs`, `test`,
  `build`. No random words, placeholders, dates or tool names. The worktree folder uses the same name as the
  branch.
- **Start:** branch from the branch checked out in the main checkout, and record that branch name. It is the merge
  target.
- **Finish:** after the user approves the diff and says yes when asked to commit and merge:
  1. Commit on the worktree branch.
  2. Rebase onto the merge target and run `tools/Validate.ps1` again, since other work may have landed since you
     started. Resolve conflicts in your own changes only. If a conflict needs another agent's work changed, stop
     and ask.
  3. Fast-forward the merge target to your branch, then ask before pushing.
  4. Remove the worktree (`git worktree remove`) and delete the branch with `git branch -d`, never `-D`. `-d`
     refuses to delete unmerged work, which is the point.
- **Abandoned work:** if the user drops the task, ask whether to delete the worktree and branch or keep them.
  Never leave one behind without saying so.
- **Start of every session:** run `git worktree list` and `git branch --no-merged <merge target>`, and report
  anything left over before starting new work.

### Commits

- Commit only after the user says yes. Ask as soon as a change is approved, and ask again before every push.
- No co-author lines. Nothing in code, comments, commit messages or branch names may hint that the work was
  generated.
