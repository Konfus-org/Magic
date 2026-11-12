---
name: investigate
description: Finds the cause of a bug, crash, error log or wrong picture before anything is fixed. Use it on your own, without being asked, whenever the user reports something broken, a run logs an error, a test fails for a reason you don't know, or a fix didn't work.
---

# Investigate

Find the cause first. Fix it second, as a slice. The commands, paths and rules in this skill are in
[PROJECT.md](../PROJECT.md).

1. **Say what is wrong:** what happens, what should happen instead, and where. If you can't say both, ask.
2. **Reproduce it** with the narrowest run that shows it, and keep that run as the slice's check:
   - A run that logs an error or crashes: the run command, with the commands that reach the error.
   - Something drawn wrong: the screenshot command. Then open the image and look at it.
   - A failing test: the command for one test project.

   If it doesn't reproduce, say what you ran and ask for the steps. Never fix what you haven't seen fail.
3. **Read the evidence** before the code: the run's log and any crash file in the logs folder. Start from the first
   error, not the last. Later errors often follow from it.
4. **Find the cause.** Follow the data back from where it is wrong to where it first goes wrong. Test one idea at a
   time with the cheapest check: a log line, a narrower run, or a value that you read while the app runs. Name the
   cause in one sentence with its `file:line` before you change any code.
5. **Fix it as a slice** ([slice skill](../slice/SKILL.md)): at the cause, not where it shows, and in its copies (How to
   implement). When the user asks for a test, write it first and see it fail. The step 2 run is the check that it
   works.
6. If a fix fails twice, stop and report what you tried and what you saw (When blocked). A question that only needs an
   answer is a `spike`: the cause goes in the summary, and nothing merges.
