# Project settings for the skills

The skills in this folder work in any repository. This file gives the values that are different in each repository.
A skill names a value by its role, for example "the validate command". Find the role here.

To use the skills in another repository, copy this folder. Then change only this file.

## Rules

| Role | Here |
| --- | --- |
| Agent rules | [AGENTS.md](../../AGENTS.md) |
| Slice size limit | [Slices](../../AGENTS.md#slices) |
| Modes | [Modes](../../AGENTS.md#modes) |
| Decisions that need the user's yes | [Decisions](../../AGENTS.md#decisions) |
| How to implement | [Implement](../../AGENTS.md#implement) |
| When blocked | [When Blocked](../../AGENTS.md#when-blocked) |
| Simplest first | [Simplest First](../../AGENTS.md#simplest-first) |
| Documentation rules | [Documentation Rules](../../AGENTS.md#documentation-rules) |
| Worktrees and claims | [Worktrees](../../AGENTS.md#worktrees) |
| Commits | [Commits](../../AGENTS.md#commits) |
| Code standards | [Code Standards](../CODE_STANDARDS.md), for `.cs` files |
| Naming | [Naming](../CODE_STANDARDS.md#naming) |
| API shape | [API shape](../CODE_STANDARDS.md#api-shape) |
| Performance rules | Code Standards, Performance |
| Other standards | [Shader Standards](../SHADER_STANDARDS.md), for `.slang` files |

## Paths

| Role | Here |
| --- | --- |
| Worktree folder | `../Magic-worktrees/<branch>` |
| Logs | `Logs/` beside the Magic executable. A crash writes `Logs/<time>.crash`. |
| Claims | `agent-claims/` in `git rev-parse --git-common-dir`. The claim command writes them. |
| Reviews that git does not track | `Build/Reviews/` |
| Profiles and dumps | `Build/profiles/` |
| One area for a review | A gem, or a folder of Core |

## Validation levels

From the lowest to the highest:

| Level | For | Runs |
| --- | --- | --- |
| `Docs` | Markdown, comments, docstrings | The build |
| `Code` | A change in behaviour that draws the same | The build, the tests, every sample headless |
| `Visual` | A change that can change what is drawn | The build, the tests, a screenshot of every sample |

The visual level is `Visual`. It prints the paths of the screenshots.

## Commands

| Role | Command |
| --- | --- |
| Validate and stamp | `tools/Scripts/AgentTaskComplete.ps1 -Change <level>` |
| Build and stamp only | `tools/Scripts/AgentTaskComplete.ps1 -Change Docs` |
| Validate one step | `tools/Scripts/Validate.ps1 -Only Build`, `Test`, `Headless` or `Screenshot` |
| Run the app | `tools/Scripts/Run.ps1 -Project <project> -Headless -Commands 'wait loaded', <commands>, 'exit'`. It exits `2` at the first error. Do not start the app in another way. |
| Take a screenshot | `tools/Scripts/Screenshot.ps1 -Project <project> -Commands <commands>` |
| Run one test project | `dotnet test --project tests/<Gem>.Tests/<Gem>.Tests.csproj` |
| Read a value while the app runs | The console command `get <setting>` |
| Claim | `tools/Scripts/Claim.ps1 -Slice <title> -Scope <paths>` |
| Release a claim | `tools/Scripts/Claim.ps1 -Release` |
| Take a profiling turn | `tools/Scripts/Profiling.ps1 -Minutes <n>`, at most 20 |
| Release a profiling turn | `tools/Scripts/Profiling.ps1 -Release` |
| The user's review | `plannotator review --base <merge target>`. `plannotator sessions --json` lists the URL and the worktree of each open review. |
| Review in a fresh context | In Claude Code, the `slice-reviewer` subagent |
| Files that every slice can touch | `src/Magic.slnx`, `Directory.Packages.props`, `Directory.Build.props`, `Directory.Build.targets`, `AGENTS.md`, `docs/` |

## Time limits

These add to the table in the [timeouts skill](timeouts/SKILL.md).

| Command | Limit |
| --- | --- |
| `dotnet build src/Magic.slnx` | 10 minutes |
| `tools/Scripts/Run.ps1` | Its `-Seconds` and one minute more, or 11 minutes |
| `tools/Scripts/Screenshot.ps1` | Two times its `-Seconds`, or 20 minutes |
| `tools/Scripts/AgentTaskComplete.ps1` or `tools/Scripts/Validate.ps1` | 60 minutes |
| `tools/Scripts/Profiling.ps1 -Minutes <n>` | 11 minutes |

A script that builds or runs Magic waits while another worktree holds a profiling turn. Add the time that the turn has
left to the limit of that script.

## Profiling

The [profiling skill](profiling/SKILL.md) uses these values.

- **Configuration:** build and run `Optimize`. Do not compare Debug numbers: Debug has the validation layer and
  overflow checks. Use `Release` only when the user asks for it.
- **Start the app:** only with `tools/Scripts/Run.ps1`. The agent hook refuses another way.
- **GPU and VRAM:** do not use `-Headless`. A headless run can use a software device.
- **Process id:** `(Get-Process Magic | Where-Object Path -like '<worktree>*').Id`.

### The check run

Magic measures itself on its `Magic.*` meters and .NET's `System.Runtime` meter (the DebugTools README). The console
command `stats <category> <seconds>` logs them.

```powershell
tools/Scripts/Run.ps1 -Configuration Optimize -Project <project> -Seconds 150 -Commands `
  'set Window.BackgroundFrameRate 0', 'set Window.MaxFrameRate 0', 'wait loaded', 'wait 10s', 'stats all 20', 'wait 61s', 'exit'
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
| GPU | `gpu.frame.duration`, `gpu.pass.duration` | GPU frame near the frame time: GPU-bound. Start with the slowest pass. `stats gpu` lists the passes. |
| VRAM | `gpu.memory.usage`, `gpu.memory.allocated`, `gpu.memory.limit` | Use against the limit. The `source` tag says if the driver measured it. |
| RAM | `process.memory.usage`, `gc.last_collection.heap.size` | Growth that does not stop is a leak. Process growth without heap growth is native. |
| Allocations | `gc.heap.total_allocated`, `gc.collections`, `gc.pause.time` | A steady frame allocates nearly nothing. Gen 0 collections during play mean per-frame allocations. |

- Over `gpu.memory.limit`, the driver moves memory to system RAM, and the GPU time jumps.
- If `gpu.memory.allocated` grows each time the same world loads, a resource is not freed.
