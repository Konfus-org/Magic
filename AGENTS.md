# AI Agent Guidelines

How agents work in this repository. The rules, paths and commands are in
[`PROJECT.md`](docs/skills/PROJECT.md). A skill names them by role, such as "Simplest first" or "the validate command":
find the role there, and read the sections that the task needs.

## Skills

A skill is a step-by-step procedure in [`docs/skills/`](docs/skills). Any agent uses one on its own, without being
asked, when its trigger fits: read its `SKILL.md` and follow it. Claude Code also finds them through `.claude/`.

| Skill | Use it |
| --- | --- |
| [kickoff](docs/skills/kickoff/SKILL.md) | A request starts something too big for one slice |
| [parallelize](docs/skills/parallelize/SKILL.md) | The user asks for a swarm, or an effort has four or more slices that do not depend on each other |
| [slice](docs/skills/slice/SKILL.md) | Any change to code, docs or tools, and the next agreed slice |
| [finish](docs/skills/finish/SKILL.md) | A single slice's code is written, an effort's last slice is in its root branch, or the user's review comments are addressed |
| [tidy-worktrees](docs/skills/tidy-worktrees/SKILL.md) | The SessionStart note lists finished worktrees |
| [investigate](docs/skills/investigate/SKILL.md) | Something is broken, logs an error or draws wrong |
| [new-gem](docs/skills/new-gem/SKILL.md) | A slice needs a new gem |
| [cleanup](docs/skills/cleanup/SKILL.md) | Each branch before QA, and the user asks to clean up, refactor, simplify or review code or an area |
| [timeouts](docs/skills/timeouts/SKILL.md) | Starting a build, a test run, a validation, a run of Magic, a wait or any background command |
| [profiling](docs/skills/profiling/SKILL.md) | Profiling, or any mention of performance, frame time, stutter, allocations, memory, VRAM or GPU time |
| [rendering](docs/skills/rendering/SKILL.md) | Any work on what the engine draws, the reference tools or the visual tests, and before you say that a picture is right |
| [qa](docs/skills/qa/SKILL.md) | Each branch before it merges, through the `qa` subagent, or the user asks for QA or a review of a branch |
| [retro](docs/skills/retro/SKILL.md) | A swarm finishes, a slice went badly, or the user corrects the workflow |
| [ste-docs](docs/skills/ste-docs/SKILL.md) | Each doc, skill or code comment that you write or rewrite. A reply needs only the rules in [Writing](#writing) |

## Always

These rules apply to each task, also when no skill is in use. The detail of each is in PROJECT.md.

- **New work starts with a skill.** A change uses the slice skill, a larger effort the kickoff skill. Understand the
  problem first. Then enter plan mode, ask the questions together, and wait for the user's yes to the plan.
- **Work in a worktree.** Make it before the first edit, also when the task started as a question. Never edit or
  commit in the main checkout. The final merge is the one exception (Worktrees).
- **One merge at the end.** Nothing merges into the main checkout's branch before the cleanup branch pass, a QA
  `PASS`, the stamp and the user's yes (the finish skill, Branches and Merges). Never push.
- **Post the status.** Post it in a code block with a progress bar, when a step finishes and at the end of each
  turn that leaves work open (Status).
- **Start Magic only with the scripts** (Commands). Give each command a time limit (the timeouts skill).
- **Commits:** the subject is `type: subject`, with no scope. No co-author lines, and nothing that hints the work was
  generated (Commits).
- **Do not write new tests** unless the user asks (Structure).

## Before You Edit

| Document | Read |
| --- | --- |
| [README](README.md) | When you do not know what Magic is, or where a thing is |
| [Code Standards](docs/CODE_STANDARDS.md) | Before editing any `.cs` file: "How we write code" and "Ground rules", then each other section the change needs |
| [Shader Standards](docs/SHADER_STANDARDS.md) | Before editing any `.slang` file, or a C# struct that a shader shares |
| [Rendering skill](docs/skills/rendering/SKILL.md) | Before any work on what the engine draws, shaders included |
| [Tests](docs/TESTS.md) | Before running or touching tests |
| [Tools](docs/TOOLS.md) | "Traps" one time, and the section of a script before you run it the first time |

Before changing an area, read its doc in `docs/`, such as [Gems](docs/GEMS.md), [Rendering](docs/RENDERING.md),
[Shaders](docs/SHADERS.md) or [Debugging](docs/DEBUGGING.md). Read a doc by the sections that the change needs, and
do not read it again in the same session.
Read a long review file by the part you need: its index first, then only that part.

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
| stamp | The record that `tools/Scripts/AgentTaskComplete.ps1` validated an exact state. |
| the user's review | The check of a slice by the user, in plannotator or in the chat. |
| swarm | Workers that build slices at the same time for one coordinator (the parallelize skill). |
| coordinator | The session that plans a swarm, gives slices to its workers and merges the root branch. |
| worker | A session that a coordinator starts: a coder that builds one slice. |
| profiling turn | A time in which one branch alone runs Magic and the tests, to profile or measure CPU, RAM or GPU use. Other branches build only at idle priority (`tools/Scripts/Profiling.ps1`). |
| QA | The check of a branch before it merges: it ranks bugs, rejects hacks and fixes nothing (the qa skill). |
| root branch | The one branch of an effort of more than one slice. Its slices go into it, and it merges into the main checkout's branch one time, at the end. |
| gem | A plugin: a part of the engine that a project adds or replaces. |
| hook | A method that the engine calls on a gem each frame (Code Standards). |
| agent hook | A check that Claude Code runs on its own (`.claude/settings.json`, PROJECT.md "Agent hooks"). |
| git hook | A check that git runs on a commit (`.githooks/`). |
