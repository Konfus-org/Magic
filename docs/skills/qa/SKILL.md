---
name: qa
description: Finds and ranks the bugs in a branch before the user reviews it, without fixing them. Use it when a swarm's coordinator starts you as its QA worker, or when the user asks for QA of a branch.
---

# QA

You find bugs and rank them. Do not fix them, and do not edit files. If the validate command formats a file, report
it as an issue and restore the file with `git restore <file>`. You get the branch, its merge target and the brief
of each slice in it. The commands in this skill are in [PROJECT.md](../PROJECT.md).

1. In the branch's worktree, run the validate and stamp command at the highest level of the slices.
   Each failure is an issue. At the visual level, open each screenshot, and compare it with the briefs.
2. If less than half of your hour is left, end with
   `SLICE: <branch>  STATE: validated  CONTEXT: <percent>  NOTE: <the failures>`. The coordinator resumes you.
3. Read `git diff <merge target>...<branch>` one slice at a time, against its brief. Run each slice's check, its
   "Works when", with the run command. Run headless where the check draws nothing.
4. Look for:
   - wrong results at the edges: empty, zero, the largest value, a missing file, a setting changed while it runs;
   - work on the wrong thread, and data that two threads share without a hand-over;
   - resources, GPU memory and subscriptions that are not freed;
   - errors that are caught and lost, and failures with no message;
   - work in a hot path, such as each frame, that allocates or does more than it needs;
   - two slices that each work but break together;
   - a sibling of a fixed bug that is not fixed, and docs that no longer match the code.
5. Report style only where it hides a bug. The cleanup worker does the rest.
6. Confirm each issue: a command that fails, a log line, a screenshot, or a code path that you can name. Mark an issue
   that you cannot confirm as `unconfirmed`.
7. Rank each issue:
   - `critical`: a crash, lost data, wrong output in the main path, or a failed build or validation;
   - `high`: wrong in a common case, a leak, or a large loss of performance;
   - `medium`: wrong in an edge case, or a doc that misleads;
   - `low`: polish.
8. Write one line for each issue, the most severe first:
   `ISSUE: <rank> | <slice> | <file:line> | <what goes wrong> | <how to see it> | <the fix you suggest>`
9. End with `SLICE: <branch>  STATE: qa-done  CONTEXT: <percent>  NOTE: <the number of issues of each rank>`.

When you are resumed with "Check these fixes:", check only those fixes and the code that they change. Report in the
same way.
