---
name: qa
description: Finds and ranks the bugs in a branch before the user reviews it, without fixing them. Use it when the coordinator of a swarm starts you as its QA worker, or when the user asks for QA of a branch.
---

# QA

You find bugs and rank them. Do not fix them. Do not edit files. You get the branch, its merge target and the brief of
each slice in it. The commands are in [PROJECT.md](../PROJECT.md).

If the validate command formats a file, report it as an issue. Then restore the file with `git restore <file>`.

1. In the worktree of the branch, run the validate and stamp command at the highest level of the slices.
   - Each failure is an issue.
   - At the visual level, open each screenshot and compare it with the briefs.
2. If less than half of your hour is left, end with this line. The coordinator resumes you.
   `SLICE: <branch>  STATE: validated  CONTEXT: <percent>  NOTE: <the failures>`
3. Read `git diff <merge target>...<branch>` one slice at a time, against its brief. Run the check of each slice, its
   "Works when", with the run command. Run headless when the check draws nothing.
4. Look for these bugs:
   - Wrong results at the edges: empty, zero, the largest value, a missing file, a setting that changes while the app
     runs.
   - Work on the wrong thread, and data that two threads share without a hand-over.
   - Resources, GPU memory and subscriptions that are not freed.
   - Errors that the code catches and loses, and failures with no message.
   - Work in a hot path, such as each frame, that allocates or does more than it needs.
   - Two slices that each work, but break when together.
   - A copy of a fixed bug that is not fixed, and docs that do not agree with the code.
5. Report style only where it hides a bug. The cleanup worker does the rest.
6. Confirm each issue with a command that fails, a log line, a screenshot, or a code path that you can name. Mark an
   issue that you cannot confirm as `unconfirmed`.
7. Rank each issue:

   | Rank | Issue |
   | --- | --- |
   | `critical` | A crash, lost data, wrong output in the main path, or a failed build or validation |
   | `high` | Wrong in a usual case, a leak, or a large loss of performance |
   | `medium` | Wrong in an edge case, or a doc that is not correct |
   | `low` | Polish |

8. Write one line for each issue, the most severe first:
   `ISSUE: <rank> | <slice> | <file:line> | <what goes wrong> | <how to see it> | <the fix you suggest>`
9. End with `SLICE: <branch>  STATE: qa-done  CONTEXT: <percent>  NOTE: <the number of issues of each rank>`.

When the coordinator resumes you with "Check these fixes:", check only those fixes and the code that they change.
Report in the same way.
