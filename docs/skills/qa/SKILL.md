---
name: qa
description: Checks a branch in a fresh context before it merges - finds and ranks its bugs, rejects hacks and fixes that hide a symptom, and judges it against its brief and the rules in PROJECT.md - without fixing anything. Each branch that merges into the main checkout's branch gets it, through the qa subagent that the finish skill starts. Use it when you are that subagent, or when the user asks for QA or a review of a branch.
---

# QA

You check one branch in its worktree, in a context that did not write it. You find bugs and rank them, reject hacks,
and give a verdict. Do not fix, edit, commit or merge. You get the worktree, the merge target and the brief of each
slice in the branch. The commands are in [PROJECT.md](../PROJECT.md).

The finish skill validates the branch after you. Do not run the validate and stamp command, the tests or a full
validation.

1. Get the worktree path, the merge target and the briefs from the request. If one is missing, find it: the worktrees
   with `git worktree list`, the merge target as the branch of the main checkout, and the briefs from the commit
   messages of the branch.
2. Read the change one slice at a time, against its brief:
   - Tracked files: `git -C <worktree> diff $(git -C <worktree> merge-base HEAD <target>)`.
   - New files: `git -C <worktree> status --porcelain`. Read each new file in full.
   - The rules in PROJECT.md for slices, modes, simplest first and how to implement, and the standards for each type of file
     that changed.

   For a root branch, also judge the whole: how its slices fit together, and what no slice owned.
3. Run the check of each slice, its "Works when", with the run command. Run headless when the check draws nothing. A
   slice of docs or comments alone has no check: compare the docs with the code.
4. Reject each hack. A hack is a fix that treats the symptom and not its cause, a shortcut that skips the work that
   the brief asks for, or a change that hides an issue. For each fix, find the cause from the brief and the code, and
   make sure that the change removes the cause. These are hacks:
   - A special case for one input, a clamp, or a null check that stops the failure but not its cause.
   - A retry, or a `catch` that is empty, only logs, or returns a default.
   - A magic constant, or a value tuned until one picture or one test looks right.
   - A stub, a hard-coded result or a `TODO` in place of the feature.
   - A test that is skipped, deleted or made looser, and a check, warning or feature that is turned off.

   These are not hacks: `// PROTO` code in a prototype slice, and a hack that the brief names and that the user
   approved.
   Write one line for each hack. A rejection blocks the merge until a fix of the cause replaces the hack:
   `REJECT: <slice> | <file:line> | <what it hides> | <the cause> | <the fix of the cause>`
5. Look for these bugs:
   - Wrong results at the edges: empty, zero, the largest value, a missing file, a setting that changes while the app
     runs.
   - Work on the wrong thread, and data that two threads share without a hand-over.
   - Resources, GPU memory and subscriptions that are not freed.
   - Errors that the code catches and loses, and failures with no message.
   - Work in a hot path, such as each frame, that allocates or does more than it needs.
   - Two slices that each work, but break when together.
   - A copy of a fixed bug that is not fixed, and docs that do not agree with the code.
6. Look for these faults in the work itself:
   - **Requirements:** the slice does not meet its check or its goal, or misses a case that its goal needs.
   - **Scope:** a change outside the slice, a merge that lost, undid or broke a change of another session (Branches
     and merges), an abstraction or general system that its mode does not allow, or more than one logical change in
     a single slice. The number of lines is not a fault.
   - **Rules:** a broken agent rule, such as a suppressed warning or edited generated code.
   - **Simpler:** the same result with clearly less code or a clearly more elegant design. Before you write it, ask:
     "Is there a more elegant solution?", and think of the version that an expert of this code would write. Use the
     changes of the [cleanup skill](../cleanup/SKILL.md). Show the better version in a few lines. Report it only
     when it is smaller or clearly cleaner and does the same job. A cleanup in the lines that the branch wrote is
     `medium`, because the branch pass of the cleanup skill must catch it. Elsewhere it is `low`.

   Do not report style, taste, names outside simplest first, or ideas to make the code stronger or more general. A
   good branch gets no findings. That is a good result.
7. Confirm each issue and each rejection with a command that fails, a log line, a screenshot, or a code path that
   you can name. Mark an issue that you cannot confirm as `unconfirmed`.
8. Rank each issue:

   | Rank | Issue |
   | --- | --- |
   | `critical` | A crash, lost data, wrong output in the main path, or a failed build or check |
   | `high` | Wrong in a usual case, a leak, a large loss of performance, a broken rule, or a missed goal |
   | `medium` | Wrong in an edge case, a doc that is not correct, a scope fault, or a cleanup in the branch's lines |
   | `low` | Polish |

9. Write the `REJECT:` lines first. Then write one line for each issue, the most severe first:
   `ISSUE: <rank> | <slice> | <file:line> | <what goes wrong> | <how to see it> | <the fix you suggest>`
10. End with these lines exactly. An agent hook reads them to record the QA pass. `PASS` means no `REJECT:` line
    and no `critical` or `high` issue.

    ```text
    WORKTREE: <the worktree's absolute path>
    VERDICT: PASS
    ```

    If not, write `VERDICT: FINDINGS (<count>)` in place of `VERDICT: PASS`.
