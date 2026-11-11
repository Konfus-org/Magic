---
name: profiling
description: Measures and improves Magic's CPU, RAM, GPU and VRAM use with the right tools and a fair before and after. Use it on your own each time a task or the user mentions profiling, performance, frame time, FPS, stutter, hitches, allocations, GC, memory use, a leak, VRAM, GPU time or something being slow.
---

# Profiling

Measure before you change anything. Change one thing. Measure again with the same check. Report the numbers.

## Rules

- Measure only during a profiling turn. Take it with `tools/Scripts/Profiling.ps1 -Minutes <n>`, at most 20. In a
  swarm, end with `STATE: needs-profiling` to get a turn (the parallelize skill).
- Release the turn with `tools/Scripts/Profiling.ps1 -Release` when you finish, also when you fail.
- Build and run `Optimize`. Do not compare Debug numbers: Debug has the validation layer and overflow checks. Use
  `Release` only when the user asks for it.
- Start Magic only with `tools/Scripts/Run.ps1`. Attach a tool to the running process by its id. Do not let a tool
  start Magic. The agent hook refuses it.
- For GPU and VRAM numbers, do not use `-Headless`. A headless run can use a software device.
- Put traces and dumps in `Build/profiles/`. Delete them when you finish, unless the user wants them.
- Give each tool a time limit (the timeouts skill).

## 1. Find the area with Magic's stats

Magic measures itself on its `Magic.*` meters and .NET's `System.Runtime` meter (the DebugTools README). The console
command `stats <category> <seconds>` logs them. Use this run as the check for every measurement:

```powershell
tools/Scripts/Run.ps1 -Configuration Optimize -Project <project> -Seconds 150 -Commands `
  'set Window.BackgroundFrameRate 0', 'set Window.MaxFrameRate 0', 'wait loaded', 'wait 10s', 'stats all 20', 'wait 61s', 'exit'
```

- The agent's window does not have the focus, so `Window.BackgroundFrameRate` caps it. The first line removes the cap.
  Keep `Window.IsVSyncOn` off.
- The wait of 10 s lets the JIT, the pipeline builds and the asset loads finish before you measure.
- Do the run 3 times. A difference smaller than the spread between runs is noise.
- On a laptop with two GPUs, check that the GPU tag names the discrete GPU. Keep the laptop on mains power.

Read these numbers (`magic.core.*`, `magic.gems.<gem>.*`, `dotnet.*`):

| Area | Instruments | What they tell |
| --- | --- | --- |
| Frame | `frame.duration`, `frame.work.duration`, `render.frame.duration` | Work near the frame time: CPU-bound. Work far below it: waits or pacing. |
| CPU | `step.duration`, `gem.hook.duration` | The step, gem and hook that take the time. |
| GPU | `gpu.frame.duration`, `gpu.pass.duration` | GPU frame near the frame time: GPU-bound. Start with the slowest pass. |
| VRAM | `gpu.memory.usage`, `gpu.memory.allocated`, `gpu.memory.limit` | Use against the limit. The `source` tag says if the driver measured it. |
| RAM | `process.memory.usage`, `gc.last_collection.heap.size` | Growth that does not stop is a leak. Process growth without heap growth is native. |
| Allocations | `gc.heap.total_allocated`, `gc.collections`, `gc.pause.time` | A steady frame allocates nearly nothing. Gen 0 collections during play mean per-frame allocations. |

For stutter, read the maximums, not the means.

## 2. Find the cause with a tool

Start the check run as a background task, then find the process id: `(Get-Process Magic | Where-Object Path -like '<worktree>*').Id`.

**CPU**

1. Record 20 s: `dotnet-trace collect -p <id> --profile dotnet-sampled-thread-time --duration 00:00:00:20 -o Build/profiles/cpu.nettrace`.
2. Read it: `dotnet-trace report Build/profiles/cpu.nettrace topN -n 25`, then again with `--inclusive`.
3. Look at the methods of the game thread and the render thread. Ignore threads that wait.

**RAM and allocations**

- To find per-frame allocations, read the code of the slow hooks for LINQ, closures, string formatting, boxing, `params`
  arrays, `new` arrays and classes, and `async` in the frame loop (Code Standards, Performance).
- To find a leak, take two heap dumps a few minutes apart:
  `dotnet-dump collect -p <id> --type Heap -o Build/profiles/a.dmp`. Compare the types that grow:
  `dotnet-dump analyze Build/profiles/a.dmp -c "dumpheap -stat" -c exit`. Find what holds one with `-c "gcroot <address>"`.

**GPU**

- Find the slowest passes in `stats gpu`.
- On an NVIDIA GPU, sample it during the same 20 s:
  `1..40 | % { nvidia-smi --query-gpu=utilization.gpu,clocks.sm,clocks_event_reasons.active,pstate,power.draw,temperature.gpu,memory.used --format=csv,noheader; Start-Sleep -Milliseconds 500 }`.
  Low use in a GPU-bound frame means stalls between the CPU and the GPU. A throttle reason makes the run unfair: do it
  again.
- Look for: passes over full-size targets that can be smaller, wide formats, overdraw, barriers wider than they must be,
  pipeline builds during play, and readbacks that wait for the same frame.

**VRAM**

- Compare `gpu.memory.usage` with `gpu.memory.limit`. Over the limit, the driver moves memory to system RAM, and the GPU
  time jumps.
- If `gpu.memory.allocated` grows each time the same world loads, a resource is not freed.

**Tools that the user runs**

RenderDoc (any GPU), Nsight Graphics (NVIDIA) and Radeon GPU Profiler (AMD) capture a frame. Windows Performance Recorder
(`wpr`, as administrator) records native and driver time. They start Magic or need the user. Do not install or start
them. If the cause needs one, ask the user, with the exact steps, through the user queue or the chat.

## 3. Change and prove

1. Make one change. Follow Code Standards, Performance.
2. Do the same check again: the same project, commands, configuration and machine state.
3. If the change does not move the number by more than the noise, revert it, unless it makes the code simpler.
4. Report a table: each metric, before and after (mean and maximum of 3 runs), and the change. Name the GPU, the
   configuration and the project.
5. Release the profiling turn.
