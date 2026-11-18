# AI Agent Guidelines

How agents work in this repository: the commands to run, how a task goes from proposal to merge, and the rules the
code and docs follow.

## Skills

A skill is a step-by-step procedure in [`docs/skills/`](docs/skills). Any agent uses one on its own, without being
asked, when its trigger fits: read its `SKILL.md` and follow it. Claude Code also finds them through `.claude/`.
[`PROJECT.md`](docs/skills/PROJECT.md) gives the commands, paths and rules that the skills name by role.

| Skill | Use it |
| --- | --- |
| [kickoff](docs/skills/kickoff/SKILL.md) | A request starts something too big for one slice |
| [parallelize](docs/skills/parallelize/SKILL.md) | The user asks for a swarm, or an effort has four or more slices that do not depend on each other |
| [slice](docs/skills/slice/SKILL.md) | Any change to code, docs or tools, and the next agreed slice |
| [finish](docs/skills/finish/SKILL.md) | A single slice's code is written, an effort's last slice is in its root branch, or the user's review comments are addressed |
| [review-slice](docs/skills/review-slice/SKILL.md) | Reviewing a built slice from a context that didn't write it |
| [tidy-worktrees](docs/skills/tidy-worktrees/SKILL.md) | The SessionStart note lists finished worktrees |
| [investigate](docs/skills/investigate/SKILL.md) | Something is broken, logs an error or draws wrong |
| [new-gem](docs/skills/new-gem/SKILL.md) | A slice needs a new gem |
| [cleanup](docs/skills/cleanup/SKILL.md) | The user asks to clean up, refactor or simplify code |
| [simplify-review](docs/skills/simplify-review/SKILL.md) | The user asks to simplify or review an area |
| [timeouts](docs/skills/timeouts/SKILL.md) | Starting a build, a test run, a validation, a run of Magic, a wait or any background command |
| [profiling](docs/skills/profiling/SKILL.md) | Profiling, or any mention of performance, frame time, stutter, allocations, memory, VRAM or GPU time |
| [qa](docs/skills/qa/SKILL.md) | A swarm's QA worker, or the user asks for QA of a branch |
| [retro](docs/skills/retro/SKILL.md) | A swarm finishes, a slice went badly, or the user corrects the workflow |
| [ste-docs](docs/skills/ste-docs/SKILL.md) | Each doc, skill or code comment that you write or rewrite. A reply needs only the rules in [Writing](#writing) |

## Commands

The scripts run under `pwsh` and Windows PowerShell ([Tools](docs/TOOLS.md)).

- **Task complete:** `tools/Scripts/AgentTaskComplete.ps1 -Change <level>` formats, runs the steps of
  `tools/Scripts/Validate.ps1` the level needs and stamps the validated state. Run it one time, when the review's fixes
  are in, and again only after a later edit or pull. A state already stamped at that level returns at once. After a
  pull with no conflict, it runs only the build.
- **Validate:** `tools/Scripts/Validate.ps1` builds, runs the tests, runs every sample project headless, then
  screenshots each and prints the paths.
- **Screenshot:** `tools/Scripts/Screenshot.ps1` runs a project until its world loads and prints the screenshot's
  path. Use `-Domains`, `-Project` and `-Commands` to reach what the samples don't show.
- **Run Magic:** start Magic only with `tools/Scripts/Run.ps1`, or with `Screenshot.ps1` and `Validate.ps1`, which run
  it themselves. They name its window with the worktree's branch, the session's name, and end the run at a time limit.
  Do not start `Magic`, `Magic.exe` or `dotnet run` on the engine or a sample in another way. `Magic new` and
  `Magic --help` open nothing, so they are allowed.
- **Run slots:** sessions build and code at the same time, but the scripts that run Magic or the tests wait for a
  free run slot first, so only `MAGIC_RUN_SLOTS` runs (1 if not set) go at the same time, from all sessions. A
  `dotnet test` of one test project takes no slot: do not run all the tests in another way.
- **Profiling:** `tools/Scripts/Profiling.ps1 -Minutes <n>` takes a profiling turn for the branch, and `-Release` ends
  it. While another branch's turn runs, the scripts that run Magic or the tests wait for it to end. On Windows, their
  builds and formats run at once, at idle priority.
- **Claim:** `tools/Scripts/Claim.ps1 -Slice <title> -Scope <paths>` tells every other session what the slice will
  change, and prints any overlap with theirs ([Worktrees](#worktrees)).
- **Configuration:** build and run `Debug`. To measure or profile performance, use `Optimize`. Use `Release` only when
  the user asks for it.
- **Time limit:** give every command you start a time limit. Never let a command run forever (the timeouts skill).
- **The user's review:** `plannotator review` opens the change as a diff in the user's browser and returns when
  they comment or approve (the finish skill).

Validation means running what the change can affect. Pick the lowest level that covers it, or the higher one when
unsure:

| `-Change` | For | Runs |
| --- | --- | --- |
| `Docs` | Markdown, comments, docstrings, and the tool files in `tools/Scripts/`, `.claude/` and `.githooks/` | Nothing. It only stamps the state |
| `Code` | Anything that runs differently but draws the same, refactors and renames included | The build, the tests, every sample project headless |
| `Visual` | Anything that can change what is drawn | The build, the tests, a screenshot of every sample project to look at |

An engine change is a change to code, shaders or assets that Magic builds or loads. A change to tools, scripts, docs,
skills or comments is not an engine change. Only an engine change gets a run of Magic. A change of Markdown, comments
or docstrings alone runs no build, no test and no run of Magic, at any step of the work. The check of a tool file is
that tool on a small input.

Merge into the main checkout's branch only once `tools/Scripts/AgentTaskComplete.ps1` has stamped that exact state.
Commits, and merges into a root branch, need no stamp.

To run one step on its own, use `tools/Scripts/Validate.ps1 -Only Build`, `Test`, `Headless` or `Screenshot`, with
`-Project` for one sample ([Tools](docs/TOOLS.md#validateps1)).

- The build must have no errors, and no warnings but unused or unreachable code's (`Directory.Build.props`). Fix a
  warning at its cause. If a warning doesn't apply, say so in your summary instead of suppressing it.

### What Runs on Its Own

In Claude Code, the agent hooks in `.claude/settings.json` hold the workflow without anyone typing a command:

- Each session, and each `/clear` or compaction, starts with the checkout and its merge target in context.
- An edit to a file in the main checkout is refused. Starting Claude Code with `MAGIC_ALLOW_MAIN_EDITS=1` allows it
  for that session; only the user sets it.
- A command that starts Magic other than through the scripts in [Run Magic](#commands) is refused.
- A `claude -p` worker run without `--model`, or on a Fable or Mythos model, is refused (the parallelize skill).
- A `dotnet build` or test run is refused while another branch's profiling turn runs. Build with
  `tools/Scripts/Validate.ps1 -Only Build` instead, which on Windows runs at idle priority and does not wait.
- An edit inside another session's claim is refused unless this worktree's own claim covers it too
  ([Worktrees](#worktrees)).
- The status line shows the checkout's branch and how full the context is.
- A `git merge` in the main checkout is refused for a branch that `tools/Scripts/AgentTaskComplete.ps1` hasn't
  stamped, or that the `slice-reviewer` subagent hasn't reviewed. A small branch needs no review: 50 changed lines or
  fewer, or a `Docs` change. Commits, and merges in a worktree, are not checked.
- A plan opens in plannotator for the user's approval.
- C# diagnostics reach you after every edit through the C# language server plugin.

## Before You Edit

| Document | Read |
| --- | --- |
| [README](README.md) | When you do not know what Magic is, or where a thing is |
| [Code Standards](docs/CODE_STANDARDS.md) | Before editing any `.cs` file: "How we write code" and "Ground rules", then each other section the change needs |
| [Shader Standards](docs/SHADER_STANDARDS.md) | Before editing any `.slang` file, or a C# struct that a shader shares |
| [Tests](docs/TESTS.md) | Before running or touching tests |
| [Tools](docs/TOOLS.md) | "Traps" one time, and the section of a script before you run it the first time |

Before changing an area, read its doc in `docs/`, such as [Gems](docs/GEMS.md), [Rendering](docs/RENDERING.md),
[Shaders](docs/SHADERS.md) or [Debugging](docs/DEBUGGING.md). Read a doc by the sections that the change needs, and
do not read it again in the same session.
Read a long review file by the part you need: its index first, then only that part.

## Workflow

| Size | What counts | Process |
| --- | --- | --- |
| Small | One file, about 20 lines or fewer, no public API change, no new dependency | In a worktree: change, validate, open the review |
| Normal | Anything larger | Brief and plan, the user's yes, then the work up to the user's review |
| Sweeping | Multi-file architectural or data-flow changes | Proposal with before/after snippets and a data-flow diagram |

### Slices

Work moves one slice at a time, and the conversation holds the plan. A swarm differs where the parallelize skill says.

- **A request is a slice, or gets split into slices.** A sweeping change becomes a list of slices you propose in the
  chat, and is never done as one task.
- **One slice is one task.** A single slice is one branch in one worktree. The slices of an effort that run one
  after the other are commits on the effort's root branch, in the root's worktree. Only a slice that runs at the same
  time as another, as a swarm's worker does, gets its own branch and worktree, and merges into the root. No slice of
  an effort merges into the main checkout's branch ([Branches and Merges](#branches-and-merges)).
- **Approved once, to build.** The user's yes to a plan, or to a list of slices, is the approval to build all that
  it covers, up to the user's review. It is never the approval to merge into the main checkout's branch. That merge
  waits for the user's review and a separate yes (the slice skill, the finish skill).
- **Iterate on the narrowest check.** While building, run one step of Validate, one test project or one sample.
  Run `tools/Scripts/AgentTaskComplete.ps1` once, on the branch that merges into the main checkout's branch: a single
  slice, or the root branch of an effort at its end.
- **Size:** one logical change: one area or one seam between two, one runnable check that shows it works, and
  reviewable in about 15 minutes. The number of changed lines is not a limit. A request that needs "and" more than once, or a design doc first, is too
  big: say so plainly and propose the split.
- **Check for drift.** Before each slice, check it against the goal and decisions agreed in the conversation. Say so
  if it doesn't serve them.
- **Go on through the approved list.** Once a slice is in the root branch, start the next slice of the list
  that the user approved. When no approved slice is left, finish the root branch (the finish skill). Never start a
  slice that the user did not approve.
- **Keep the context small.** Start the next slice in the same session while the context is less than half full.
  Otherwise give its brief to paste after `/clear`, and stop. When compacting, keep the slice list, the current
  slice, its worktree path, the files it changed and its `-Change` level.

### Kickoff

A request that starts a new effort too big for one slice begins with an interview, then a list of slices, riskiest
first (the kickoff skill). An effort runs as a swarm when the user asks for one, or when four or more of its slices
do not depend on each other (the parallelize skill). A smaller effort runs one slice after the other.

### Modes

Every slice names its mode.

| Mode | For | Allowed | Not allowed |
| --- | --- | --- | --- |
| `spike` | Answering one question | Hard-coded values, copy-paste, no tests, no cleanup | Merging: the answer goes in the summary |
| `prototype` | Proving a feature works end to end | Hard-coded values behind one member, marked `// PROTO` | New abstractions, generic systems, work for later needs |
| `production` | Code a second feature already depends on | Everything these docs ask for | Anything hacky the summary doesn't name |

`prototype` is the default. A prototype becomes `production` only when a second feature depends on it, in a slice of
its own. In a `spike`, skip [Leave it cleaner](#implement). In a `prototype`, it covers only the lines the slice
itself writes.

Push back when the user polishes early: a refactor, rename, generalization or doc rewrite of `spike` or `prototype`
code nothing depends on yet is named as a follow-up in the summary, not done in the current slice.

### Decisions

Check the request's premises and look for an existing tool before building (the slice skill). Hard-to-reverse
decisions need an explicit yes before you build on them: public API, file formats, a new dependency or gem, and the
architecture. State the decision and the alternative, and put the decision and its reason in the commit message.

### Implement

Make your worktree before the first edit ([Worktrees](#worktrees)), even if the task started as a question. State
assumptions and decisions a reviewer would question as you make them. For a search across many files, use a search
subagent and keep only its answer.

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

What merges into the main checkout's branch, a single slice or an effort's root branch, is reviewed one time in
a fresh context unless it is small, validated one time, shown to the user, and merges only on their yes (the finish
skill). A slice of an effort only builds, runs its check and goes into the root branch (the slice skill).

### When Blocked

- A build or test failure outside your files: stop and report it. Don't fix other agents' work.
- An ambiguity that changes the design: ask. Otherwise pick the reasonable reading and say which.
- The slice grows past its one logical change, needs files outside its scope, or runs past its time-box: stop and report
  what exists. The user decides to continue, split or drop it.
- The same fix has failed twice: stop and report instead of trying a third time.

## Design Rules

### Simplest First

Agents tend to over-build. Less is more: the best change often removes more code than it adds.

- **Remove before you add.** First look for code the change makes unnecessary, and for a way to do the job by
  deleting, merging or reusing what exists. A slice that ends with fewer lines than it started with is a good slice.
- **Look for the elegant version.** Before writing, ask whether there is a more elegant solution, not just a smaller
  one: a better data shape, an existing service or .NET feature, or a reframing that makes the special cases
  disappear. Brute force, such as repeated branches, copied blocks or a pile of cases, is a sign to stop and rethink.
- **Start from the obvious version.** Name the smallest change that could work: extend a method before making a new
  one, a local before a field. Build that, and add more only when a check fails without it.
- **No flags that steer behaviour.** No `bool` field or parameter that makes a method do something else, and no
  field that records what already happened (`_initialized`, `_isDirty`, `_skipNext`, `_hasWarned`). Write two
  methods, let the caller choose, or derive the answer from data you already have. A field holds data the type
  owns, never a mode.
- **No state you can compute.** Never cache, mirror or track a value that can be read or worked out when needed,
  unless a measurement shows the cost.
- **Every new member has a caller in this slice.** No property, overload, option, setting, event or parameter for a
  later need. No property that only wraps a field, no interface or base type with one implementation, no type that
  forwards calls. Public API a gem or game could use is the one exception.
- **Don't guard against what can't happen.** No null check on a value that can't be null, no `try`/`catch` that only
  logs or swallows, no fallback for a case the code never reaches.
- **Pick the API shape by the call site.** Plain data in an initializer first; steps, a fluent chain or a builder
  only where [API shape](docs/CODE_STANDARDS.md#api-shape) says.
- **Name it for what it is.** Use the words the code already uses ([Naming](docs/CODE_STANDARDS.md#naming)). No
  `Manager`, `Helper`, `Handler`, `Util`, `Info` or `Impl` names, and no `New`, `2` or `Internal` suffixes. If a name
  needs a comment to make sense, rename it.
- **When the simple fix doesn't fit, say so.** If the code around it blocks the obvious change, stop and report that
  instead of routing around it with a flag, a special case or a parallel path.
- **Cut before you finish.** Once it works, read your diff as its reviewer, ask again whether there is a more
  elegant solution, and delete everything the slice runs without: members, parameters, flags, branches, checks,
  logging, comments. The smaller diff wins.

### Structure

- **Match the surrounding style.**
- **Project conventions first, then .NET's** (the Framework Design Guidelines).
- **One responsibility each.** Split interfaces by what their user needs. Gems may depend on each other and reference one
  another; a gem throws when a dependency it needs isn't there.
- **Stateless where possible.** State that must exist follows [Code Standards](docs/CODE_STANDARDS.md#how-we-write-code).
- **Data-oriented, explicit data flow.** Only the inheritance, interfaces and indirection the problem needs.
- **Dependencies come from NuGet.** Never vendor or copy external code.
- **Tests:** don't write new tests unless asked. Run the existing tests that cover what you changed. When a test is
  asked for with a fix, write it first and see it fail ([Tests](docs/TESTS.md)).

## Documentation Rules

- A doc says what a thing is and how to use it, in short plain sentences. No reasons, history or design decisions.
- No inventories: never list the files, gems, assets, settings or types in a folder.
- No copies of code: no member, option or default lists; point to the code, `--help` or the settings class.
- Outside configuration files, name another gem by its role ("the GPU provider"), never by its name.
- Docs change in the same change as the code.

## Writing

Write in Simplified Technical English. For a reply, a summary or a plan in the chat, these rules are enough:

- Give the answer or the result first, in one sentence.
- Write short sentences in the active voice, with simple tenses and one topic each.
- Use one name for one thing, and simple, common words.
- Use a list for separate items, a table to compare items, and a Mermaid diagram for a flow.
- Give only what the reader needs. Do not tell the steps that you did.

Read the [ste-docs skill](docs/skills/ste-docs/SKILL.md) when you write or rewrite a doc, a skill or code comments.
Use the words in the glossary only with their meanings there.

### Glossary

| Term | Meaning |
| --- | --- |
| slice | One task: its own branch and worktree, or its commits on the root branch of an effort (the slice skill). |
| brief | The short statement of a slice: its goal, mode, scope, check and `-Change` level. |
| main checkout | The first checkout of the repository. Agents do not edit in it. |
| worktree | The git worktree that one branch is built in, under `Magic-worktrees/`. |
| merge target | The branch that a branch merges into: its root branch for a slice of an effort, otherwise the branch of the main checkout. |
| claim | The record of the paths that a slice changes (`tools/Scripts/Claim.ps1`). |
| stamp | The record that `tools/Scripts/AgentTaskComplete.ps1` validated an exact state. |
| review | The check of a slice by a reviewer in a fresh context (the review-slice skill). |
| the user's review | The check of a slice by the user, in plannotator or in the chat. |
| swarm | Workers that build slices at the same time for one coordinator (the parallelize skill). |
| coordinator | The session that plans a swarm, gives slices to its workers and merges the root branch. |
| worker | A session that a coordinator starts: a coder that builds one slice, or QA. |
| profiling turn | A time in which one branch alone runs Magic and the tests, to profile or measure CPU, RAM or GPU use. Other branches build only at idle priority (`tools/Scripts/Profiling.ps1`). |
| QA | The check of a swarm's root for bugs by a worker that ranks them and fixes nothing (the qa skill). |
| root branch | The one branch of an effort of more than one slice. Its slices go into it, and it merges into the main checkout's branch one time, at the end. |
| gem | A plugin: a part of the engine that a project adds or replaces. |
| hook | A method that the engine calls on a gem each frame (Code Standards). |
| agent hook | A check that Claude Code runs on its own (`.claude/settings.json`). |
| git hook | A check that git runs on a commit (`.githooks/`). |

## Git

### Branches and Merges

This is a hard rule. Each agent, skill and worker obeys it. No skill or plugin overrides it.

- **One effort, one root branch.** An effort of more than one slice, such as a kickoff's slice list or a swarm, gets
  one root branch from the main checkout's branch before its first slice. Slices that run one after the other commit
  on the root. Each worker of a swarm branches from the root with `--track`, and merges back into the root. A request
  of one slice is its own branch and needs no root.
- **Pull, do not merge.** A branch can pull the main checkout's branch into itself at any time. Nothing merges into
  the main checkout's branch while the work goes on: not after a slice, a review or a fix, and not because a skill
  says to finish a branch.
- **One merge at the end.** When the work is complete and settled, the session that owns the branch (the coordinator,
  or the session that ran the slices) does these steps (the finish skill):
  1. Pulls the main checkout's branch into its branch, and resolves the conflicts.
  2. Gets one review in a fresh context, then runs `tools/Scripts/AgentTaskComplete.ps1` one time, at the highest
     level of its slices.
  3. Asks the user if it can merge, in the user's review or in the chat, and waits for a yes.
  4. In the main checkout, runs `git merge --no-ff <branch>` one time. Then it removes the branch's worktree and
     branch.
- **Never push.**

### Worktrees

Every task runs in a worktree: its own, or the root's for a slice of an effort. Never edit or commit in the main
checkout. The final merge is the one exception.

- **Name:** `<type>/<feature>` in kebab-case, for example `fix/material-cache-leak`. Types: `feature`, `fix`,
  `refactor`, `docs`, `test`, `build`, `ci`. The worktree folder has the branch's name.
- **Start:** start the session from the repository's root, never from a folder in it. Branch as
  [Branches and Merges](#branches-and-merges) says. Title the session with the branch's name, as
  `fix/material-cache-leak`.
- **Claim:** right after making the worktree, claim the files and folders the slice will change:
  `tools/Scripts/Claim.ps1 -Slice <title> -Scope <paths>`. Include any file every slice touches that you need:
  `src/Magic.slnx`, `Directory.Packages.props`, `Directory.Build.props`, `Directory.Build.targets`, `AGENTS.md` and
  `docs/`. Claim again when the scope changes: a new claim replaces the old one. If it reports overlap with another
  claim or worktree, stop and ask the user whether to wait for that work, narrow the slice, or go ahead. Never edit
  inside another session's claim without that yes.
- **Release:** hold a claim only while you work in its scope.
  - In an effort, each slice claims only its own scope when it starts. This releases the scope of the slice before.
  - Release the claim with `-Release` as soon as the slice is answered or dropped, and as the last step of your work.
    Before your final report, make sure that no claim of yours is left. A coordinator releases a worker's claim with
    `-Release -Branch <branch>`.
  - A claim also ends on its own: the scripts delete it when its branch merges or its worktree is removed. After 3
    days with no commit and no new claim, a claim is idle and no longer blocks. Claim again to keep it.
- **Finish** and merge as [Branches and Merges](#branches-and-merges) and the finish skill say.
- **Abandoned work:** if the user drops the task, release its claim, leave the worktree and branch and say so.
- **Start of a session:** when the SessionStart note lists finished worktrees, remove what is
  finished and report the rest (the tidy-worktrees skill). Never remove a worktree or branch with work found nowhere
  else.

### Sessions

A session is done when it isn't running, its work has merged or its question was answered, and nothing in it waits
on the user. Delete the side sessions you started once they're done, with the user's approval of each (the
tidy-worktrees skill). A coordinator closes its own workers without a new approval (the parallelize skill).

### Commits

- The subject starts with `type: `, as in `fix: `, which the repository's git hook writes from the branch's type and the
  files staged ([Git hooks](docs/TOOLS.md#git-hooks)): `feature/` gives `feat`, every other branch type its own word,
  and a commit of Markdown alone `docs`. Write the subject alone, never a scope or area prefix such as `fix(core): `
  or `Core, Gems:`. The `commit-msg` git hook refuses a commit whose subject lacks the type or has a scope, and the
  Commits check a push.
- Commit on your own branch as often as it helps. Only the merge into the main checkout's branch waits for the
  user ([Branches and Merges](#branches-and-merges)).
- Never push.
- No co-author lines, and nothing in code, comments, commit messages or branch names that hints the work was
  generated.
