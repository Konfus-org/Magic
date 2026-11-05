# Plan

The goal, the decisions, the buckets of slices and the log. Agents read this at the start of every session and work
only on the slices it lists ([AGENTS.md](AGENTS.md#slices)).

## Goal

Set by the kickoff interview. Changes only with the user's explicit yes.

- **For:** TODO: the small game, toy or learning goal Magic serves right now
- **Decision it drives:** TODO
- **First thing a player does:** TODO
- **Out of scope for now:** TODO

## Decisions

One line each, newest first: `YYYY-MM-DD · decision · reason`.

- 2026-11-04 · No start-up check of C# structs against their Slang twins; `[ShaderStruct]` removed · nothing read it,
  all 18 twins match today, and a check is about 250 lines and a new `IShaderCompiler` method for 3.4–6.8 s of cold
  module loading.

## Buckets

### Current: TODO

TODO: the bucket's goal in one sentence.

- [ ] TODO: first slice (`prototype`)
- [ ] Cleanup of what this bucket used

### Later

One line each. Not sliced until they're current.

- TODO

## Cleanup

Anything noticed mid-slice that isn't the slice's job.

- `GpuVertex` and `GpuCluster` are in Core, and their Slang twins are in the GI renderer's gem.

## Log

One line each, newest first: `YYYY-MM-DD · slice · result · notes`. Past about 30 lines, fold the oldest into one
paragraph.

- 2026-11-04 · Correct the shader struct docs · done · removed `[ShaderStruct]` and the doc's mention of `ILight`.
