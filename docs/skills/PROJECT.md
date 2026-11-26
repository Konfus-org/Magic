# Project settings for the skills

The skills in this folder work in any repository. This file gives the rules, paths and commands that are different in
each repository. A skill names a value or a rule by its role, for example "the validate command" or "Simplest first".
Find the role here. Read the sections that the task needs.

To use the skills in another repository, copy this folder. Then change only this file and the skill map in the
agent rules ([AGENTS.md](../../AGENTS.md)).

## Rules

The [code standards](../CODE_STANDARDS.md) apply to `.cs` files, with [Naming](../CODE_STANDARDS.md#naming) and
[API shape](../CODE_STANDARDS.md#api-shape). Their Performance section holds the performance rules. The
[shader standards](../SHADER_STANDARDS.md) apply to `.slang` files.

### Slices

Work moves one slice at a time, and the conversation holds the plan. A swarm differs where the parallelize skill says.

| Size | What counts | Process |
| --- | --- | --- |
| Small | One file, about 20 lines or fewer, no public API change, no new dependency | In a worktree: change, then the finish skill |
| Normal | Anything larger | In plan mode: understand the problem, the questions together, the brief and plan, the user's yes. Then the work up to the user's review |
| Sweeping | Multi-file architectural or data-flow changes | In plan mode: a proposal with before/after snippets and a data-flow diagram |

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
  Run the validate and stamp command once, on the branch that merges into the main checkout's branch: a single
  slice, or the root branch of an effort at its end.
- **Slice size limit:** one logical change: one area or one seam between two, one runnable check that shows it
  works, and reviewable in about 15 minutes. The number of changed lines is not a limit. A request that needs "and"
  more than once, or a design doc first, is too big: say so plainly and propose the split.
- **Check for drift.** Before each slice, check it against the goal and decisions agreed in the conversation. Say so
  if it doesn't serve them.
- **Go on through the approved list.** Once a slice is in the root branch, start the next slice of the list
  that the user approved. When no approved slice is left, finish the root branch (the finish skill). Never start a
  slice that the user did not approve.
- **Keep the context small.** Start the next slice in the same session while the context is less than half full.
  Otherwise give its brief to paste after `/clear`, and stop. When compacting, keep the slice list, the current
  slice, its worktree path, the files it changed and its `-Change` level.

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

### When Blocked

- A build or test failure outside your files: stop and report it. Don't fix other agents' work.
- An ambiguity that changes the design: ask. Otherwise pick the reasonable reading and say which.
- The slice grows past its one logical change, needs files outside its scope, or runs past its time-box: stop and
  report what exists. The user decides to continue, split or drop it.
- The same fix has failed twice: stop and report instead of trying a third time.

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
  only where [API shape](../CODE_STANDARDS.md#api-shape) says.
- **Name it for what it is.** Use the words the code already uses ([Naming](../CODE_STANDARDS.md#naming)). No
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
- **One responsibility each.** Split interfaces by what their user needs. Gems may depend on each other and reference
  one another; a gem throws when a dependency it needs isn't there.
- **Stateless where possible.** State that must exist follows [Code Standards](../CODE_STANDARDS.md#how-we-write-code).
- **Data-oriented, explicit data flow.** Only the inheritance, interfaces and indirection the problem needs.
- **Dependencies come from NuGet.** Never vendor or copy external code.
- **Tests:** don't write new tests unless asked. Run the existing tests that cover what you changed. When a test is
  asked for with a fix, write it first and see it fail ([Tests](../TESTS.md)).

### Documentation Rules

- A doc says what a thing is and how to use it, in short plain sentences. No reasons, history or design decisions.
- No inventories: never list the files, gems, assets, settings or types in a folder.
- No copies of code: no member, option or default lists; point to the code, `--help` or the settings class.
- Outside configuration files, name another gem by its role ("the GPU provider"), never by its name.
- Docs change in the same change as the code.

### Branches and Merges

This is a hard rule. Each agent, skill and worker obeys it. No skill or plugin overrides it.

- **One effort, one root branch.** An effort of more than one slice, such as a kickoff's slice list or a swarm, gets
  one root branch from the main checkout's branch before its first slice. Slices that run one after the other commit
  on the root. Each worker of a swarm branches from the root with `--track`, and merges back into the root. A request
  of one slice is its own branch and needs no root.
- **Pull, do not merge.** A branch can pull the main checkout's branch into itself at any time. Nothing merges into
  the main checkout's branch while the work goes on: not after a slice, QA or a fix, and not because a skill
  says to finish a branch.
- **Apply both changes.** A pull, and a merge into a root branch, joins your work with the work of other sessions.
  Each side's change must hold in the result: what it added, what it changed and what it deleted.
  - Read the other change before you resolve a conflict with it. Never take one side of a conflict whole to make the
    merge pass, and never undo the other change because it is in the way.
  - Code that the other change deleted, moved or renamed stays deleted, moved or renamed. If your change uses the old
    code, make your change again on what replaced it. Do not bring the old code back, and do not keep a copy of it.
  - Git joins lines, not behaviour, so a merge with no conflict can still be wrong. After each one, build. Then run
    the checks of each area that the two sides both changed.
  - A fault that the merge brings into your branch is yours. Fix it before the branch merges further.
  - If the two changes cannot both stand, stop and ask the user.
- **One merge at the end.** When the work is complete and settled, the session that owns the branch (the coordinator,
  or the session that ran the slices) merges it into the main checkout's branch one time, with the finish skill:
  after the cleanup branch pass, a QA `PASS`, the stamp and the user's yes. The branch becomes one commit.

### Worktrees

Every task runs in a worktree: its own, or the root's for a slice of an effort. Never edit or commit in the main
checkout. The final merge is the one exception.

- **Name:** `<type>/<feature>` in kebab-case, for example `fix/material-cache-leak`. Types: `feature`, `fix`,
  `refactor`, `docs`, `test`, `build`, `ci`. The worktree folder has the branch's name.
- **Start:** start the session from the repository's root, never from a folder in it. Branch as
  [Branches and Merges](#branches-and-merges) says. Title the session with the branch's name, as
  `fix/material-cache-leak`.
- **Other sessions:** other sessions change the same repository at the same time, each in its own worktree. Edit the
  file that the change belongs in, also when another session changes it too. The pull joins the two changes
  ([Branches and Merges](#branches-and-merges)).
- **Abandoned work:** if the user drops the task, leave the worktree and branch and say so.
- **Start of a session:** when the SessionStart note lists finished worktrees, remove what is
  finished and report the rest (the tidy-worktrees skill). Never remove a worktree or branch with work found nowhere
  else.

### Sessions

A session is done when it isn't running, its work has merged or its question was answered, and nothing in it waits
on the user. Delete the side sessions you started once they're done, with the user's approval of each (the
tidy-worktrees skill). A coordinator closes its own workers without a new approval (the parallelize skill).

### Status

Each agent posts its status, so that the user sees what waits on them and how much work is left. Post it in a
`text` code block, as in this example. Put no other text in the block.

```text
[########------------]  40%  2/5 steps
Waiting on you: nothing
Doing: add the Status section
Next: update the parallelize skill
```

- **The bar:** 20 cells, `#` for done and `-` for not done. Then the percent and the count.
- **The count:** the steps of the agent's task list that are done, of all its steps. A coordinator counts the
  slices that merged into the root or answered, of all the approved slices (the parallelize skill).
- **The first line under the bar** is always `Waiting on you:`. Write `nothing` when nothing waits on the user.
- **When:** each time a step finishes, at least one time in about ten minutes of work, and at the end of each turn
  that leaves work open.
- **Closing lines:** the status comes before the closing lines that a skill requires, such as the `VERDICT:` lines
  of QA and the `SLICE:` line of a worker. Those lines stay last.

### Commits

- The subject starts with `type: `, as in `fix: `, which the repository's git hook writes from the branch's type and
  the files staged ([Git hooks](../TOOLS.md#git-hooks)): `feature/` gives `feat`, every other branch type its own
  word, and a commit of Markdown alone `docs`. Write the subject alone, never a scope or area prefix such as
  `fix(core): ` or `Core, Gems:`. The `commit-msg` git hook refuses a commit whose subject lacks the type or has a
  scope, and the Commits check a push.
- Commit on your own branch as often as it helps. Only the merge into the main checkout's branch waits for the
  user ([Branches and Merges](#branches-and-merges)). That merge makes one commit of the branch, so the main
  checkout's branch gets one commit for each single slice or effort.
- Never push.
- No co-author lines, and nothing in code, comments, commit messages or branch names that hints the work was
  generated.

## Paths

| Role | Here |
| --- | --- |
| Worktree folder | `../Magic-worktrees/<branch>` |
| Logs | `Logs/` beside the Magic executable. Each run writes `Logs/<time>_<process id>.log`, and `query log` prints its path. A crash writes `Logs/<time>.crash`. |
| Reviews that git does not track | `Build/Reviews/` |
| Profiles and dumps | `Build/profiles/` |
| One area for a review | A gem, or a folder of Core |

## Validation

Validation means running what the change can affect. The validate and stamp command takes the level as `-Change`.
The levels go from the lowest to the highest. The visual level is `Visual`. Pick the lowest level that covers the
change, or the higher one when unsure:

| `-Change` | For | Runs |
| --- | --- | --- |
| `Docs` | Markdown, comments, docstrings, and the tool files in `tools/Scripts/`, `.claude/` and `.githooks/` | Nothing. It only stamps the state |
| `Code` | Anything that runs differently but draws the same, refactors and renames included | The build, the tests, every sample project headless |
| `Visual` | Anything that can change what is drawn | The build, the tests, a screenshot of every sample project to look at. It prints the paths of the screenshots |

- An engine change is a change to code, shaders or assets that Magic builds or loads. A change to tools, scripts,
  docs, skills or comments is not an engine change. Only an engine change gets a run of Magic. A change of Markdown,
  comments or docstrings alone runs no build, no test and no run of Magic, at any step of the work. The check of a
  tool file is that tool on a small input.
- The validate and stamp command formats, runs the steps of the validate command that the level needs, and stamps
  the validated state. Run it one time, when QA's fixes are in, and again only after a later edit or pull. A state
  already stamped at that level returns at once. After a pull with no conflict, it runs only the build.
- Merge into the main checkout's branch only once the command has stamped that exact state. Commits, and merges into
  a root branch, need no stamp.
- The build must have no errors, and no warnings but unused or unreachable code's (`Directory.Build.props`). Fix a
  warning at its cause. If a warning doesn't apply, say so in your summary instead of suppressing it.
- **Configuration:** build and run `Debug`. To measure or profile performance, use `Optimize`. Use `Release` only
  when the user asks for it.

## Commands

The scripts run under `pwsh` and Windows PowerShell ([Tools](../TOOLS.md)).

| Role | Command |
| --- | --- |
| Validate and stamp | `tools/Scripts/AgentTaskComplete.ps1 -Change <level>` |
| Stamp only, for docs and comments | `tools/Scripts/AgentTaskComplete.ps1 -Change Docs` |
| Validate | `tools/Scripts/Validate.ps1` builds, runs the tests, runs every sample project headless, then screenshots each and prints the paths |
| Validate one step | `tools/Scripts/Validate.ps1 -Only Build`, `Test`, `Headless` or `Screenshot`, with `-Project` for one sample ([Tools](../TOOLS.md#validateps1)) |
| Run the app | `tools/Scripts/Run.ps1 -Project <project> -Headless -Commands 'wait loaded', <commands>, 'exit'`. It exits `2` at the first error |
| Take a screenshot | `tools/Scripts/Screenshot.ps1 -Project <project> -Commands <commands>`. It runs a project until its world loads and prints the screenshot's path. Use `-Domains` to reach what the samples don't show |
| Run one test project | `dotnet test --project tests/<Gem>.Tests/<Gem>.Tests.csproj` |
| Read a value while the app runs | The console command `query <setting>` |
| Take a profiling turn | `tools/Scripts/Profiling.ps1 -Minutes <n>`, at most 20 |
| Release a profiling turn | `tools/Scripts/Profiling.ps1 -Release` |
| The user's review | `plannotator review --base <merge target>`. It opens the change as a diff in the user's browser and returns when they comment or approve. `plannotator sessions --json` lists the URL and the worktree of each open review. |
| QA in a fresh context | In Claude Code, the `qa` subagent, in the foreground |
| The final merge | `git merge --squash <branch>`, then `git commit -m "<type>: <subject>"`, each in a command of its own, in the main checkout |
| Is a branch's work in its merge target | `git merge-tree --write-tree <merge target> <branch>` prints the same tree as `git rev-parse "<merge target>^{tree}"` |
| Files that every slice can touch | `src/Magic.slnx`, `Directory.Packages.props`, `Directory.Build.props`, `Directory.Build.targets`, `AGENTS.md`, `docs/` |

- **Run Magic:** start Magic only with `tools/Scripts/Run.ps1`, or with `Screenshot.ps1` and `Validate.ps1`, which
  run it themselves. They name its window with the worktree's branch and the session's name, and end the run at a
  time limit. Do not start `Magic`, `Magic.exe` or `dotnet run` on the engine or a sample in another way. `Magic new`
  and `Magic --help` open nothing, so they are allowed.
- **Run slots:** sessions build and code at the same time, but the scripts that run Magic or the tests wait for a
  free run slot first, so only `MAGIC_RUN_SLOTS` runs (1 if not set) go at the same time, from all sessions. A
  `dotnet test` of one test project takes no slot: do not run all the tests in another way.
- **Profiling turns:** while another branch's turn runs, the scripts that run Magic or the tests wait for it to end.
  On Windows, their builds and formats run at once, at idle priority.

## Agent hooks

In Claude Code, the agent hooks in `.claude/settings.json` hold the workflow without anyone typing a command:

- Each session, and each `/clear` or compaction, starts with the checkout and its merge target in context.
- An edit to a file in the main checkout is refused. Starting Claude Code with `MAGIC_ALLOW_MAIN_EDITS=1` allows it
  for that session; only the user sets it.
- A command that starts Magic other than through the scripts in [Commands](#commands) is refused.
- A `claude -p` worker run without `--model`, or on a Fable or Mythos model, is refused (the parallelize skill).
- A `dotnet build` or test run is refused while another branch's profiling turn runs. Build with
  `tools/Scripts/Validate.ps1 -Only Build` instead, which on Windows runs at idle priority and does not wait.
- The status line shows the checkout's branch and how full the context is.
- A `git merge` in the main checkout is refused for a branch that the validate and stamp command hasn't stamped, or
  whose last verdict from the `qa` subagent is not `PASS`. Each branch needs it, also a small one or a `Docs`
  change. Commits, and merges in a worktree, are not checked.
- A plan opens in plannotator for the user's approval.
- C# diagnostics reach you after every edit through the C# language server plugin.

## Time limits

These add to the table in the [timeouts skill](timeouts/SKILL.md).

| Command | Limit |
| --- | --- |
| `dotnet build src/Magic.slnx` | 10 minutes |
| `tools/Scripts/Run.ps1` | Its `-Seconds` and one minute more, or 11 minutes |
| `tools/Scripts/Screenshot.ps1` | Two times its `-Seconds`, or 20 minutes |
| `tools/Scripts/AgentTaskComplete.ps1` or `tools/Scripts/Validate.ps1` | 60 minutes |
| `tools/Scripts/Profiling.ps1 -Minutes <n>` | 11 minutes |

A script that runs Magic or the tests waits while another worktree holds a profiling turn. Add the time that the turn has
left to the limit of that script.

## Profiling

The [profiling skill](profiling/SKILL.md) uses these values.

- **Configuration:** build and run `Optimize`. Do not compare Debug numbers: Debug has the validation layer and
  overflow checks. Use `Release` only when the user asks for it.
- **Start the app:** only with `tools/Scripts/Run.ps1`. The agent hook refuses another way.
- **GPU and VRAM:** do not use `-Headless`. A headless run can use a software device.
- **Process id:** `(Get-Process Magic | Where-Object Path -like '<worktree>*').Id`.

### The check run

Magic measures itself on its `Magic.*` meters and .NET's `System.Runtime` meter ([Debugging](../DEBUGGING.md#measuring)). The console
command `query stats <category> --every <seconds>` logs them.

```powershell
tools/Scripts/Run.ps1 -Configuration Optimize -Project <project> -Seconds 150 -Commands `
  'set Window.BackgroundFrameRate 0', 'set Window.MaxFrameRate 0', 'wait loaded', 'wait 10s', 'query stats all --every 20', 'wait 61s', 'exit'
```

- The agent's window does not have the focus, so `Window.BackgroundFrameRate` caps it. The first command removes the
  cap. Keep `Window.IsVSyncOn` off.
- On a laptop with two GPUs, make sure that the GPU tag names the discrete GPU.

### The numbers

Read `magic.core.*`, `magic.gems.<gem>.*` and `dotnet.*`.

| Area | Instruments | What they tell |
| --- | --- | --- |
| Frame | `frame.duration`, `frame.work.duration`, `render.frame.duration` | Work near the frame time: CPU-bound. Work far below it: waits or pacing. |
| CPU | `step.duration`, `gem.hook.duration` | The step, gem and hook that take the time. |
| GPU | `gpu.frame.duration`, `gpu.pass.duration` | GPU frame near the frame time: GPU-bound. Start with the slowest pass. `query stats gpu` lists the passes. |
| VRAM | `gpu.memory.usage`, `gpu.memory.allocated`, `gpu.memory.limit` | Use against the limit. The `source` tag says if the driver measured it. |
| RAM | `process.memory.usage`, `gc.last_collection.heap.size` | Growth that does not stop is a leak. Process growth without heap growth is native. |
| Allocations | `gc.heap.total_allocated`, `gc.collections`, `gc.pause.time` | A steady frame allocates nearly nothing. Gen 0 collections during play mean per-frame allocations. |

- Over `gpu.memory.limit`, the driver moves memory to system RAM, and the GPU time jumps.
- If `gpu.memory.allocated` grows each time the same world loads, a resource is not freed.

## Rendering

The [rendering skill](rendering/SKILL.md) and its [fault list](rendering/FAULTS.md) use these values.

- **The reference:** Blender Cycles, through the reference scene script.
- **The test scene:** the Cabin domain of `samples/MountainCabin/MountainCabin.magic`.

### Engine invariants

Each row is true of Magic's renderer. A change to one is a sweeping change and needs the user's yes. The rules for
shaders and the structs that they share with C# are in [Shader Standards](../SHADER_STANDARDS.md).

| Invariant | Magic | Evidence |
| --- | --- | --- |
| Host | C#. Vulkan through Vortice.Vulkan. Slang through its C library, in process | [VulkanGraphics.csproj:15](../../src/Gems/VulkanGraphics/VulkanGraphics.csproj#L15), [Slang.cs:14](../../src/Gems/SlangShaderCompiler/Slang.cs#L14) |
| Axes | Left-handed: +Y up, +X right, +Z forward. 1 unit is 1 m. Front faces are clockwise | [Transform.cs:6-10](../../src/Core/Ecs/Components/Transform.cs#L6), [VulkanResources.cs:596](../../src/Gems/VulkanGraphics/VulkanResources.cs#L596) |
| Matrices | Row vectors (System.Numerics). Slang compiles row-major | [FrameView.cs:203](../../src/Gems/Rendering/DeferredRendering/View/FrameView.cs#L203), [ShaderCompiler.cs:650](../../src/Gems/SlangShaderCompiler/ShaderCompiler.cs#L650) |
| Depth | Reverse-Z with no far plane: 1 at the near plane, 0 at infinity. `D32_SFLOAT`, `GREATER_OR_EQUAL`, a clear to 0 | [FrameView.cs:203-218](../../src/Gems/Rendering/DeferredRendering/View/FrameView.cs#L203), [Renderer.cs:621](../../src/Gems/Rendering/DeferredRendering/Renderer.cs#L621), [VulkanResources.cs:599-603](../../src/Gems/VulkanGraphics/VulkanResources.cs#L599), [GpuCommands.cs:158](../../src/Core/Graphics/GpuCommands.cs#L158) |
| API | Vulkan 1.3 or later, with `synchronization2` and dynamic rendering. No `VkRenderPass` | [VulkanDevice.cs:478](../../src/Gems/VulkanGraphics/VulkanDevice.cs#L478), [VulkanDevice.cs:523-528](../../src/Gems/VulkanGraphics/VulkanDevice.cs#L523), [VulkanRecorder.cs:310](../../src/Gems/VulkanGraphics/VulkanRecorder.cs#L310) |
| Barriers | A pass declares what it reads and writes. The GPU provider puts in the barriers from these uses. Never write a barrier by hand | [ComputePass.cs:9](../../src/Core/Graphics/ComputePass.cs#L9), [GpuCommands.cs:111](../../src/Core/Graphics/GpuCommands.cs#L111) |
| Bindings | One bindless heap in set 0, and a push constant of 16 bytes. A shader reads each resource through a heap reference | [Heap.slang:18-26](../../src/Resources/Shaders/Core/Heap.slang#L18), [Pass.slang:15](../../src/Resources/Shaders/Core/Pass.slang#L15), [GpuCommands.cs:191](../../src/Core/Graphics/GpuCommands.cs#L191) |
| Shared structs | Scalar buffer layout, the same bytes as a C# struct with `Pack = 4`. Matrices row-major | [ShaderCompiler.cs:662](../../src/Gems/SlangShaderCompiler/ShaderCompiler.cs#L662), [Heap.slang:6-7](../../src/Resources/Shaders/Core/Heap.slang#L6) |
| Shaders | Profile `spirv_1_6`, entry point names kept, warnings as errors | [ShaderCompiler.cs:639-653](../../src/Gems/SlangShaderCompiler/ShaderCompiler.cs#L639) |
| Resources | A pass asks for each texture and buffer by name and description in each frame. The same description gives the same resource, so history carries over. A resource that no frame asks for is destroyed | [IGraphicsApi.cs:28-37](../../src/Core/Graphics/IGraphicsApi.cs#L28) |
| Rays | Inline `RayQuery` alone: no ray tracing pipeline, no shader binding table. Rays are optional: check `GpuFeatures.HasRayQuery` before you build or trace | [HitSurface.slang:74](../../src/Gems/Rendering/DeferredRendering/Resources/Shaders/Trace/HitSurface.slang#L74), [VulkanDevice.cs:539-545](../../src/Gems/VulkanGraphics/VulkanDevice.cs#L539), [GlobalIllumination/Renderer.cs:128](../../src/Gems/Rendering/GlobalIllumination/Renderer.cs#L128) |

### Files

| Role | Here |
| --- | --- |
| The views file | `tools/Scripts/ReferenceViews.json`: each view's name, project, entity, hours and named world points |
| The limits file | `tools/Scripts/VisualTests.json`: the pass limits, the frame counts, the waits, Cycles' samples, and the values of the scene that a capture holds still |
| The previews folder | `tools/Reference/Previews`: `<view>-<hour>-cycles.jpg`, the reference of each view and hour as a photograph |
| The pictures of a run | `Screenshots/Reference/<view>-<hour>` beside Magic: Magic's picture, the reference's, the split picture, the ratio map, the FLIP map and the metrics |
| The report page of a run | `Screenshots/Reference/report.html` beside Magic |
| The baseline | `tools/Reference/Baseline`: each row's value from a run whose pictures the user approved |
| The references | `tools/Reference/References`: `<view>-<hour>.exr`, its mask and `manifest.json`, which git keeps with Git LFS |
| The script that renders them | `tools/Scripts/UpdateReferences.ps1`, the one script that runs Cycles |
| Blender | `C:/Program Files (x86)/Steam/steamapps/common/Blender/blender.exe` |

### Views

Each view is posed by an entity named `ReferenceView.<Name>` with the tag `render-reference` in the Cabin domain's
`globals.prefab`. To add a view or a point, follow [Reference views](../DEBUGGING.md#reference-views).

| View | Where |
| --- | --- |
| `cabin-wall` | Inside, at the window wall |
| `cabin-window-glow` | Outside, at a window |
| `cabin-room` | Inside, over the carpet, with the turning plate in the frame |
| `cabin-surfaces` | Inside, at the table that carries the test surfaces |
| `cabin-surfaces-close` | Inside, 0.85 m from the table's surfaces, at hours 12 and 22 |
| `cabin-lantern` | Inside, 1 m from the lantern on the bench, at hours 12 and 22 |
| `cabin-outside` | Outside, at the door |
| `cabin-trees` | In a gap at the clearing's edge, 40 m from the cabin, towards the forest |
| `leak-room` | In a sealed room 10 m behind the cabin. Each of its points states the light it may hold at most |

The hours are 8, 12, 16 and 22, or those that a view states. The turns are at 6, 20, 120 and 360 degrees a second. The slow two show a trail
and a shadow that swims, and the fast two show what a mouse turn does. Each turn has the same limits, which are in
the limits file.

`cabin-room` and `cabin-surfaces` also glide 1.5 m along the camera's own right axis in 2 seconds. This move
uncovers what nearer things hid, which a turn does not do.

A point of a view can be a leak point, which `"maxNits"` marks. Its leak is then a row of its own, which the
`leakNits` limit judges. The sealed room states one on each of its points: its reference is black, so the rows that
compare the two pictures mean nothing there, and the view is measured at its own pose alone.

### The look

The scene leans to stylized realism. Each value is in the scene's data, which Magic and the Blender script both
read ([The look](rendering/SKILL.md#the-look)).

| Value | Where | In the cabin |
| --- | --- | --- |
| The sky's light | `Brightness` of the `Sky` in the domain's `globals.prefab` | 1.6, against 1 for the air itself |
| The fire | The cabin's prefab | 760 cd, a range of 16 m, the colour of 2700 K |
| The lantern | The cabin's prefab | 320 cd, a range of 12 m, the colour of 2600 K |
| The preview's grade | `grade` in the limits file | More saturation and contrast, warm highlights, cool shadows |

The sun and the moon take no part of this. Magic's own window shows no grade.

### Commands

| Role | Command |
| --- | --- |
| The visual tests | `tools/Scripts/VisualTests.ps1`, with a list in `-View` and in `-Hour` for part of the set, and `-Tier Quick` for the fast tier |
| The references | `tools/Scripts/UpdateReferences.ps1`, with `-View`, `-Hour`, `-Samples` and `-All`. It is the one script that runs Cycles |
| The judging and the report page of a run | `tools/Scripts/JudgeVisualTests.ps1 -Run <the folder of a run>`, which `VisualTests.ps1` calls at the end of a run |
| The comparison of one view | `tools/Scripts/VisualTests.ps1 -View <view> -Hour <hour> -Tier Quick` |
| Magic's HDR picture | The console command `screenshot --hdr`, through `tools/Scripts/Screenshot.ps1` |
| The reference scene, every view and hour from one build of it | `tools/Reference/BuildScene.py --jobs <list of views and hours>`, through Blender |
| Cycles' own settings, by name | `tools/Reference/BuildScene.py --settings <JSON>`, or a `settings` key of a job |
| The light at an hour | The console commands `set Day.Hour <hour>`, then `query DeferredRendering.Sun` |
| The comparison | `tools/Reference/Compare.py`, through Blender: the ratio map, the split picture, the preview and the metrics |
| The world point of a pixel | `tools/Reference/Compare.py --view <view> --pose <7 numbers> --pick <x> <y> --depth <screenshot>` |
| The settle and the edge noise | `tools/Reference/MeasureDenoise.py --moves <list> --out <folder>` |

Run a Blender script as `blender.exe -b --python <script> -- <arguments>`, always with a time limit.

### The engine's own commands

A tool or an agent asks Magic what Magic knows. The form of each command, and what its JSON holds, is in
[Supported Commands and Options](../CLI.md#supported-commands-and-options).

| Command | Gives |
| --- | --- |
| `query domain [--names <name>...] [--tags <tag>...]` | The open domain as the engine resolves it, as one JSON object |
| `query components` | Each component type that the run knows, with each field's name, type and default, and the same for a material |
| `query asset <path>`, `query asset --id <id>` | An asset's id, path and type |
| `import <file>` | The id, the path and the type of a file as an asset. A file that the project holds already gets its `.meta` where it stands |
| `new asset <file>` | The id, the path and the type of a new asset file, with the values of Magic's default asset of its type |
| `query DeferredRendering.Sun` | The light that lights the frame: the direction towards it, its lux above the air and what it is to the sky |
| `query ProceduralSky.Brightness` | What the sky's light is multiplied by |

A gem adds a subject of `query` or a command of its own through `ConsoleCommands` ([Gems](../GEMS.md)).

Each `query` takes `--out <file>`. Get the JSON from a headless run:

```powershell
tools/Scripts/Run.ps1 -Headless -Project samples/MountainCabin/MountainCabin.magic -Commands 'wait loaded', 'query domain --tags render-reference --out views.json', 'exit'
```

`query` and `set` reach one entity by its tag or by its id, never by its name, and a tag is case sensitive.

### Time limits

| Command | Limit |
| --- | --- |
| One Cycles render of this scene | 8 to 14 minutes |
| `tools/Scripts/JudgeVisualTests.ps1` on the folder of a run | 2 minutes |
| `tools/Scripts/VisualTests.ps1 -Tier Quick` for one view and hour | 30 minutes |
| `tools/Scripts/VisualTests.ps1` for one view and hour | 90 minutes |
| `tools/Scripts/VisualTests.ps1` for every view and hour | 7 hours |

A command that runs Magic waits for a free run slot first, and its limit must hold that wait.

### Debug views and switches

`DeferredRendering.RenderView` shows one part of a frame: `Final`, `Albedo`, `Normal`, `Depth`, `Roughness`, `Metalness`,
`Occlusion`, `Velocity`, `IndirectDiffuse`, `Reflections` and `CascadeAtlas`. `DeferredRendering.DepthViewFar` sets the distance at
which the `Depth` view turns black. A setting's key starts with the category of its own member, or with the category of its
settings class when the member names none.

These values turn off one term of the light, to measure each one alone. Set each with `set <value> false`. A
`GlobalIllumination` value is state of the run and is not saved:

| Value | What it turns off |
| --- | --- |
| `Rendering.IsTracedShadowsOn` | The sun's traced shadows |
| `Rendering.IsShadowMapsOn` | The shadow maps' shadows |
| `Rendering.IsIndirectLightOn` | The light that bounces |
| `Rendering.IsReflectionsOn` | The traced reflections |
| `GlobalIllumination.IsHitProbeLightOn` | The probes' light at a diffuse ray's hit |
| `GlobalIllumination.IsDiffuseSkyOn` | The sky on a diffuse ray that reaches nothing |
| `GlobalIllumination.IsShadowFillOn` | The fill that a shadowed surface gets |
| `GlobalIllumination.IsPixelProbeLightOn` | The probes' light where the traced light does not cover a pixel |
