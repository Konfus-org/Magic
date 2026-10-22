# AI Agent Guidelines

How agents work in this repository: the commands to run, how a task goes from proposal to merge, and the rules
the code and docs follow. Most rules give their reason. When a case isn't covered, follow the reason.

## Commands

The tools run under `pwsh` and Windows PowerShell. [Tools](tools/README.md) covers them in full, including where
the `Magic` executable is built.

- **Format:** `tools/Format.ps1` formats the C# a change touches by `.editorconfig`: it orders the members and the
  usings, applies the code style, lays out blank lines and `if`/`else` bodies, wraps lines that are too long and fixes
  the whitespace. Agents never need to run it on its own, as the next command does.
- **Validate:** `tools/Validate.ps1` builds `Magic.slnx`, checks the whole tree's formatting, runs the tests, runs the
  samples headless through every one of their domains until each has loaded, and runs them with a window through every
  domain, ending in a screenshot of each whose paths it prints. It reports each step's exit code. Every step must pass,
  and the screenshots must be looked at, before work is done.
- **Task complete:** `tools/AgentTaskComplete.ps1` formats the change, runs all of Validate and, when it passes,
  stamps the exact state it validated. Run it on completing a task, and again after any later edit or rebase.
- **Screenshot:** `tools/Screenshot.ps1` runs a project with a window until its world has loaded and prints the
  screenshot's path; `-Domains` opens other domains after it, a screenshot of each, and `-Project` and `-Commands`
  reach what the samples alone do not show.
- **Review:** `plannotator review` opens the change in the user's browser as a diff they comment on line by line,
  and returns when they send their comments or approve ([Finish](#finish)).

Validation means the app ran with the change and you looked at it working: a build, tests or a run that ends before
the world has loaded prove nothing about what is drawn. A hook refuses an agent's `git commit` and `git merge` unless
`tools/AgentTaskComplete.ps1` stamped that exact state ([Tools](tools/README.md#agenttaskcompleteps1)).

What Validate runs, for when you need one step on its own:

```powershell
dotnet build Magic.slnx
dotnet format Magic.slnx --verify-no-changes --no-restore
dotnet test Magic.slnx
'wait loaded', 'open Domains/Audio/Audio.domain', 'quit' | Magic --project samples --seconds 1200 --fail-on-error --headless
tools/Screenshot.ps1 -Domains 'Domains/Audio/Audio.domain'
```

`--fail-on-error` returns exit code `2` if anything was logged as an error during the run.

The build must finish with no warnings or errors. Fix a warning at its cause. If you believe a warning does not
apply, say so in your summary rather than suppressing it. A wrap the formatter makes that reads badly is reshaped by
hand: it breaks a line at the first place that fits, which is not always where a person would.

## Before You Edit

Read the document that covers what you are about to change:

| Document | Read | Purpose & Scope |
| --- | --- | --- |
| [Start Here](README.md) | Once per session | What Magic is, how to build and run it, a tour of how it works with links into Core's README, and the folder layout. |
| [Code Standards](CODESTANDARDS.md) | Before editing any `.cs` file | C# coding standards, performance rules, and cross-platform guidelines enforced by the build system. |
| [Contributing](CONTRIBUTING.md) | Before presenting a change for review | Code review processes and acceptance criteria. |
| [Tests](tests/README.md) | Before running or touching tests | Test suites, conventions, and requirements for requested test coverage. |
| [Tools](tools/README.md) | Before running a tool script | What `tools/Validate.ps1`, `tools/AgentTaskComplete.ps1` and `tools/Screenshot.ps1` run, and where the `Magic` executable is built. |

Most gems, asset root folders and other large systems have their own `README.md` explaining how that area works
and what must hold for it to work. Before changing anything in an area, read every `README.md` on the path from
that area's root folder down to the folder you are editing. If you change how the area
works, update that README in the same change.

## Workflow

Scale the process to the size of the change.

| Size | What counts | Process |
| --- | --- | --- |
| Small | One file, roughly 20 lines or fewer, no public API change, no new dependency | In a worktree, make the change, validate, open the review |
| Normal | Anything larger | Proposal first, in plan mode; no edits until the user approves |
| Sweeping | Multi-file architectural or data-flow changes | Proposal with before/after snippets and a data-flow diagram |

### Check the Request

A request is a direction to check, not a conclusion to repeat. Verify its premises against the code, the docs and
what you can establish, and say plainly when it is not the right direction, with the evidence, before doing
anything. Agreement is only worth something when it could have been disagreement. Where a current .NET or C#
feature does the job better than what the request names (a collection expression, a `ref struct`, a `SearchValues`),
say so, since the request may predate the feature.

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

Make your worktree before the first edit (see [Worktrees](#worktrees)), even when the task began as a check, a
validation run or a question that turned up something to fix. State assumptions and any decision a reviewer would
want to question as you make it.

- **Fix the siblings.** When a fix applies to one member, look for the same code elsewhere (an overload, the same
  pattern further down the file, the same step in another gem) and fix it in the same change, so the bug does not
  live on in the copies.
- **Never edit generated code.** A file the build writes (the native bindings it generates under `Build/obj/`,
  anything marked `<auto-generated>`) is changed by changing what generates it: the generator, the headers list, the
  csproj item. An edit to the output lasts until the next build.
- **Leave it cleaner than you found it.** Quality goes one small tolerated flaw at a time, and the next reader pays
  for each one left behind, so the code a task touches is in better shape after it. For an agent that needs
  boundaries, since it also conflicts with the smallest change and with other agents' work:
  - **Where:** inside the members and files the task already changes, and what they directly call. Not a sweep of
    the folder, the gem or the repository.
  - **What:** what these documents already forbid and you are now looking at: a misleading name, a stale comment or
    one that restates its line, a block whose purpose is not obvious with no comment saying it, a dead member, a `!`,
    a nested `if` a guard clause flattens, a duplicate of the line you are fixing, a docstring the change made wrong.
  - **How:** never a change in behaviour, and never mixed in silently. The summary and the diff review say which
    hunks are the fix and which are the cleanup, so a reviewer can accept one and question the other.
  - **When to stop:** a cleanup that grows past a few lines, crosses into files the task does not touch, or needs a
    design decision is not folded in. Name it in the summary and propose it as its own task. Another agent's
    in-flight work, anything in another worktree or on an unmerged branch, is never touched.

### Finish

1. Run `tools/AgentTaskComplete.ps1`, which formats the change before it validates it, so the review shows the
   formatted code. A failing test is fixed at its cause, never loosened, skipped or deleted.
2. Open the screenshots `tools/AgentTaskComplete.ps1` printed, one of each sample, and look at them, for every change,
   not only visual ones: the app ran with your change, and what they show is the check. Where the change shows only
   somewhere the samples do not reach, a failure path, a debug view or another project, screenshot that too, with
   `tools/Screenshot.ps1 -Project` or `-Commands`, and look at it.
3. Summarize what changed and why, what you assumed, anything you could not verify, and what the screenshots show,
   with the fix told apart from the cleanup.
4. Open the review: from the worktree, run `plannotator review --base <merge target>` in the background. Against
   the merge target it shows everything the merge would bring, the branch's commits and every uncommitted and
   untracked file, so nothing needs staging. It opens the user's browser on the diff, where they or another person
   comment on lines and then send the comments or approve, and the command returns with what they chose. Wait for
   it rather than ask in the conversation. If it is stopped before they decide, run it again: their comments are
   kept as a draft.
5. Comments come back by file and line. Address each one, or say with evidence why not, validate again, and go
   back to step 3. A review closed without comments or approval is neither: ask what the user wants.
6. Approval is the yes to commit and merge, and so is "approved" typed in the conversation: do both at once as
   [Worktrees](#worktrees) says, without asking again.

### When Blocked

- If a build or test failure is outside the files you changed, stop and report it. Do not fix other agents' work.
- If an ambiguity would change the design, ask. If it would not, pick the reasonable reading and say which one
  you took.

## Design Rules

- **Simplest robust solution.** Prefer removing or simplifying code over adding it. Match the surrounding style
  and patterns. Add no abstraction until a second real use needs it, and no member for a caller that does not exist
  yet.
- **Project conventions first, then .NET's.** Where these documents and `.editorconfig` are silent, follow .NET's
  own conventions (the Framework Design Guidelines) rather than inventing a house rule, so the code reads as any C#
  developer expects.
- **One responsibility each.** A type does one thing, and an interface is split by what its user needs rather than by
  who implements it, so a gem takes only what it uses and gems depend on Core's interfaces, never on each other
  (the single-responsibility, interface-segregation and dependency-inversion principles of SOLID, as they apply
  here).
- **Stateless where possible.** State that must exist follows [Code Standards](CODESTANDARDS.md#state).
- **Data-oriented, explicit data flow.** Keep inheritance, interfaces and indirection to what the problem needs.
- **Dependencies come from NuGet.** Do not vendor or copy external code, so every dependency stays versioned and
  updatable in one place.
- **Tests:** do not write new tests unless the user asks. Always run the existing tests that cover code you
  changed. When a test is asked for with a fix, write it first and see it fail without the fix, so it proves
  something ([Tests](tests/README.md)).

## Documentation Rules

- **Docs change with the code.** Update comments, docstrings and `.md` files in the same change, so they stay
  accurate.
- **Explain, never inventory.** Docs say how a thing works and what must hold for it to work, never what is in
  which folder, file or namespace. No lists or tables of the files, assets, ids, types or packages there are, and
  no folder trees beyond where each kind of thing goes. For example, the Materials README explains how materials
  work, not which materials exist. The folder already shows what is in it, and a list is one more thing to keep in
  step that quietly goes out of date. Name a specific file only as the example that teaches a rule, or where the
  behaviour being explained depends on that path.
- **Concepts, not code.** A doc explains the architecture, the purpose and the workflow: what a thing is for, how
  it works and what must hold for it to work. It does not list members, signatures, parameters, keys, options or
  defaults: the code is the reference, and a doc that copies it is a second copy to keep in step. No snippet that
  shows calling an API, and no payload written out.
- **Write for longevity.** Assume the code changes tomorrow. A doc stays accurate when a member is renamed, a
  parameter added or a value tuned, so it names a thing only where the name is the concept being explained (the
  service a gem takes, a hook, an asset type, a file extension), never as a pointer into today's code. A number
  appears only where the reasoning in the sentence is about that number, never as a budget, a limit or a default.
- **Placeholders for what the code states.** Where a reader needs the exact shape, say where it lives rather than
  copying it: a type's doc comments, which the build writes beside every assembly, document its members and a
  setting's default; `--help` lists the command line; a shader's header lists its macros; the assets beside a
  README are its examples. "The keys are the `Material` type's properties" outlives any table of them.
- **Gems by role, never by name.** Gems can be swapped at any time, so comments, summaries, docs and user-facing
  messages describe another gem only by what it does ("a platform gem", "the GPU provider", "a shader compiler"),
  never by its name or the library it wraps. A gem names itself only inside its own folder. Configuration that
  must list gems (project files, `Magic.slnx`, `.editorconfig` sections) is exempt.

## Git

### Worktrees

Each task runs in its own worktree, so concurrent agents never see each other's uncommitted changes, and the main
checkout's branch moves only by a fast-forward of finished work. The order is fixed: the worktree first, on a branch
with a clear name; then the work; then the merge back; then the worktree and branch go. Nothing is edited in the
main checkout, not even a one-line fix, and nothing is committed on its branch directly. A worktree lives only as
long as its task, or, when the task is dropped, until the user decides at the next session's start.

- **Name:** `<type>/<feature>`, kebab-case, describing the change in a few words, for example
  `fix/material-cache-leak` or `feature/shader-hot-reload`. Types: `feature`, `fix`, `refactor`, `docs`, `test`,
  `build`. No random words, placeholders, dates or tool names. The worktree folder uses the same name as the
  branch.
- **Start:** branch from the branch checked out in the main checkout, and record that branch name. It is the merge
  target.
- **Finish:** as soon as the user approves the review:
  1. Commit on the worktree branch.
  2. Rebase onto the merge target and run `tools/AgentTaskComplete.ps1` again, since other work may have landed
     since you started, and look at its new screenshots: the rebased result is what merges, so it is what must be seen working.
     Resolve conflicts in your own changes only. If a conflict needs another agent's work changed, stop and ask.
  3. Fast-forward the merge target to your branch, and push nothing ([Commits](#commits)).
  4. Remove the worktree (`git worktree remove`) and delete the branch with `git branch -d`, never `-D`. `-d`
     refuses to delete unmerged work, which is the point.
  5. Remove every other worktree and branch made for the task the same way, such as the one the app running the
     session made for it at the start, which stays behind even when the work was done in a worktree of your own. A
     branch whose commits are all on a remote branch, as one cut from the remote's default branch is, goes with
     `-d` too once its upstream is set to that remote branch, since `-d` then sees it merged.
  6. Delete the side sessions you started that are done ([Sessions](#sessions)).
- **Abandoned work:** if the user drops the task, leave the worktree and branch in place and say so. The next
  session's start-of-session check reports them, and the user decides then.
- **Start of every session:** run `git worktree list` and `git branch --no-merged <merge target>`, and look at the
  repository's sessions your tools can see. Clear away what is dead before starting new work: a worktree or branch
  whose session is gone and whose commits are all merged, into the merge target or a remote branch, is removed as in
  step 5 of Finish, and a session that is done is deleted. Report the rest. A worktree or branch holding commits or
  changes found nowhere else is never removed: the user decides.

### Sessions

A session is one agent's thread of work, and the user reads the list of them to see what is still going, so a session
left there once it is done hides the ones that are not. A session is done when it is not running, its work has merged
or its question was answered, and nothing in it waits on the user. One that was interrupted, never began its work, or
waits on the user for an approval, an answer or a push stays, however old it is.

- **Delete what is done.** A session deletes the side sessions it started once they are done, and the
  start-of-session check deletes any other that is. The user approves each delete.
- **Keep what is still going.** Deleting a session can archive the side sessions it started along with it, so the
  ones still going are detached to the top level first, by you or the user, or the session is left until they are
  done.
- **Check what a delete left.** Deleting a session can leave its worktree behind, detached, and its branch with it:
  look at `git worktree list` afterwards and remove them as in step 5 of Finish.

### Commits

- Commit only once the user has approved the review. The approval is the yes, so do not ask for another.
- Never push. The user pushes.
- No co-author lines. Nothing in code, comments, commit messages or branch names may hint that the work was
  generated.
