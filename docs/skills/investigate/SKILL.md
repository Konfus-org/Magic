---
name: investigate
description: Finds the cause of a bug, crash, error log or wrong picture before anything is fixed. Use it on your own, without being asked, whenever the user reports something broken, a run logs an error, a test fails for a reason you don't know, or a fix didn't work.
---

# Investigate

Find the cause first; fix it second, as a slice.

1. **Say what is wrong:** what happens, what should happen instead, and where. If you can't say both, ask.
2. **Reproduce it** with the narrowest run that shows it, and keep that run as the slice's check:
   - A run that logs an error or crashes: `'wait loaded', <commands>, 'exit' | Build/bin/Debug/Magic open
     <project> --headless --fail-on-error`, which exits `2` at the first error.
   - Something drawn wrong: `tools/Scripts/Screenshot.ps1 -Project <project> -Commands <commands>`, then open the
     image and look at it.
   - A failing test: `dotnet test --project tests/<Gem>.Tests/<Gem>.Tests.csproj`.

   If it doesn't reproduce, say what you ran and ask for the steps. Never fix what you haven't seen fail.
3. **Read the evidence** before the code: the run's log in `Logs/` beside Magic, and any `Logs/<time>.crash`. Start
   from the first error, not the last; later ones often follow from it.
4. **Find the cause.** Follow the data back from where it is wrong to where it first goes wrong. Test one idea at a
   time with the cheapest check: a log line, a narrower command, a value read in the console with `get`. Name the cause
   in one sentence with its `file:line` before changing any code.
5. **Fix it as a slice** ([slice skill](../slice/SKILL.md)): at the cause, not where it shows, and in its copies
   ([AGENTS.md](../../../AGENTS.md#implement)). When the user asks for a test, write it first and see it fail. The
   step 2 run is the check that it works.
6. If a fix fails twice, stop and report what you tried and what you saw
   ([AGENTS.md](../../../AGENTS.md#when-blocked)). A question that only needs an answer is a `spike`: the cause goes
   in the summary, and nothing merges.
