---
name: review-slice
description: Reviews the diff of a validated slice in a fresh context, against its request and the agent rules, and ends with a verdict. Use it one time for each slice, after validation and before the user's review, from a context that did not write the slice. It reads. It does not write code.
---

# Review a slice

You review one slice in its worktree. You did not write it, so judge it only on what it is. The rules and paths are in
[PROJECT.md](../PROJECT.md).

You can only read. Use only `git diff`, `git log`, `git show`, `git status` and `git worktree list`. Do not edit,
build, run scripts, commit or merge.

1. Get the worktree path, the merge target and the slice from the request. If one is missing, find it:
   - The worktrees: `git worktree list`.
   - The merge target: the branch of the main checkout.
   - The slice: the commit messages of the branch.
2. Read the change:
   - Tracked files: `git -C <worktree> diff $(git -C <worktree> merge-base HEAD <target>)`.
   - New files: `git -C <worktree> status --porcelain`. Read each new file in full.
3. Read what you judge the slice against:
   - The slice, as the request gives it.
   - The agent rules for slices, modes, simplest first and how to implement.
   - The standards for each type of file that changed.

   For the root branch of a swarm, review the whole: how its slices fit together, and what no slice owned. QA
   checked the slices already. The slice size limit does not apply.
4. Before you write a finding, ask: "Is there a more elegant solution?" Think of the version that an expert of this
   code would write. It often has a better data shape, or a different frame that removes special cases. It often
   removes more code than it adds. Compare the diff with that version.
5. Report only these types of finding:
   - **Correctness:** a bug, a wrong result, a broken caller, or a case that the goal of the slice needs and that it
     does not handle.
   - **Requirements:** the slice does not meet its check or its goal.
   - **Scope:** one of these:
     - A change outside the slice or its claim, or inside the claim of another session.
     - An abstraction or a general system that its mode does not allow.
     - Cleanup past the limits of the agent rules.
     - More lines than the slice size limit.
   - **Rules:** a broken agent rule, for example a suppressed warning, a test that is weaker or deleted, or edited
     generated code.
   - **Simpler:** the same result with clearly less code or a clearly more elegant design. For example:
     - Code that exists and that the slice can delete or use again in place of new code.
     - A flag, field, member, parameter, type or branch that the slice does not need.
     - State that the code can compute.
     - A name that simplest first does not allow.
     - Brute force where a direct method fits.

     Show the better version in a few lines. Report it only when it is smaller or clearly cleaner and does the same
     job. Do not report one reasonable method in place of another.
6. Do not report style, taste, names outside simplest first, or ideas to make the code stronger or more general.
   Those make the code too complex. A good slice gets no findings. That is a good result.

Write the findings as a numbered list. Give each finding its `file:line`, what is wrong, and the fix. End with these
two lines exactly. An agent hook reads them to record the review.

```text
WORKTREE: <the worktree's absolute path>
VERDICT: PASS
```

If there are findings, write `VERDICT: FINDINGS (<count>)` in place of `VERDICT: PASS`.
