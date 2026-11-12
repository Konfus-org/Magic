---
name: profiling
description: Measures and improves an app's CPU, RAM, GPU and VRAM use with the right tools and a fair before and after. Use it on your own each time a task or the user mentions profiling, performance, frame time, FPS, stutter, hitches, allocations, GC, memory use, a leak, VRAM, GPU time or something being slow.
---

# Profiling

Measure before you change anything. Change one thing. Measure again with the same check. Report the numbers.

The commands, the configuration, the check run and the numbers to read are in the Profiling part of
[PROJECT.md](../PROJECT.md).

## Rules

- Measure only during a profiling turn, if the project has one. Take it with the profiling turn command. In a swarm,
  end with `STATE: needs-profiling` to get a turn (the parallelize skill).
- Release the turn when you finish, also when you fail.
- Build and run the configuration for profiling. Do not compare debug numbers.
- Start the app only with the run command. Attach a tool to the running process by its id. Do not let a tool start
  the app.
- Put traces and dumps in the profiles folder. Delete them when you finish, unless the user wants them.
- Give each tool a time limit (the timeouts skill).

## 1. Find the area

1. Use the check run as the check for every measurement.
2. Let the JIT, the startup work and the loads finish before you measure.
3. Do the run 3 times. A difference smaller than the spread between runs is noise.
4. On a laptop, keep it on mains power.
5. Read the numbers. For stutter, read the maximums, not the means.

## 2. Find the cause with a tool

Start the check run as a background task. Then find the process id.

**CPU**

1. Record 20 s: `dotnet-trace collect -p <id> --profile dotnet-sampled-thread-time --duration 00:00:00:20 -o <profiles>/cpu.nettrace`.
2. Read it: `dotnet-trace report <profiles>/cpu.nettrace topN -n 25`, then again with `--inclusive`.
3. Look at the methods of the threads that do the work, for example the main thread and the render thread. Ignore
   threads that wait.

**RAM and allocations**

- To find allocations in a hot path, read its code for LINQ, closures, string formatting, boxing, `params` arrays,
  `new` arrays and classes, and `async` (the performance rules).
- To find a leak, take two heap dumps a few minutes apart:
  `dotnet-dump collect -p <id> --type Heap -o <profiles>/a.dmp`. Compare the types that grow:
  `dotnet-dump analyze <profiles>/a.dmp -c "dumpheap -stat" -c exit`. Find what holds one with `-c "gcroot <address>"`.

**GPU**

- Find the slowest passes in the project's GPU numbers.
- On an NVIDIA GPU, sample it during the same 20 s:
  `1..40 | % { nvidia-smi --query-gpu=utilization.gpu,clocks.sm,clocks_event_reasons.active,pstate,power.draw,temperature.gpu,memory.used --format=csv,noheader; Start-Sleep -Milliseconds 500 }`.
  Low use in a GPU-bound frame means stalls between the CPU and the GPU. A throttle reason makes the run unfair: do it
  again.
- Look for: passes over full-size targets that can be smaller, wide formats, overdraw, barriers wider than they must be,
  pipeline builds during play, and readbacks that wait for the same frame.

**VRAM**

- Compare the VRAM use with the limit. Over the limit, the driver moves memory to system RAM, and the GPU time jumps.
- If the allocated VRAM grows each time the same content loads, a resource is not freed.

**Tools that the user runs**

RenderDoc (any GPU), Nsight Graphics (NVIDIA) and Radeon GPU Profiler (AMD) capture a frame. Windows Performance Recorder
(`wpr`, as administrator) records native and driver time. They start the app or need the user. Do not install or start
them. If the cause needs one, ask the user, with the exact steps, through the user queue or the chat.

## 3. Change and prove

1. Make one change. Follow the performance rules.
2. Do the same check again: the same project, commands, configuration and machine state.
3. If the change does not move the number by more than the noise, revert it, unless it makes the code simpler.
4. Report a table: each metric, before and after (mean and maximum of 3 runs), and the change. Name the GPU, the
   configuration and the project.
5. Release the profiling turn.
