---
name: review-slice
description: Reviews a validated slice's diff in a fresh context against its request and the agent rules, and ends with a verdict. Use it once per slice, after validation and before the user's review, from a context that didn't write the slice. It reads; it never writes code.
---

# Review a slice

You review one slice in its worktree. The rules and paths in this skill are in [PROJECT.md](../PROJECT.md). You didn't write it, so judge it on its own terms.

1. Take the worktree path, the merge target and the slice from the request. If one is missing, find it: `git worktree
   list` for the worktrees, the main checkout's branch for the target, the branch's commit messages for the slice.
2. Read the change: `git -C <worktree> diff $(git -C <worktree> merge-base HEAD <target>)` for tracked files, and
   `git -C <worktree> status --porcelain` for new ones, which you read in full.
3. Read what it is judged against: the slice as the request states it, the agent rules for slices, modes, simplest first
   and how to implement, and the standards for each kind of file that changed. A swarm's root branch is reviewed as one integration: how its slices fit together and what none of them
   owned. QA checked its slices already, and the size limit doesn't apply.
4. Before listing anything, ask: is there a more elegant solution? Picture the version someone who knows the code
   base well would write: often a better data shape or a reframing that makes cases disappear, and often one that
   removes more code than it adds. Compare the diff to it.
5. Report only these:
   - **Correctness:** a bug, a wrong result, a broken caller, a case the slice's own goal needs that isn't handled.
   - **Requirements:** the slice's runnable check or goal isn't met.
   - **Scope:** a change outside the slice or its claim, a change inside another session's claim, an abstraction or
     generality its mode forbids, cleanup past the agent rules' limits, or more than the slice size limit.
   - **Rules:** an agent rule broken, such as a suppressed warning, a loosened or deleted test, or edited
     generated code.
   - **Simpler:** the same result with clearly less code or a clearly more elegant design: existing code the slice
     could delete or reuse instead of adding, a flag, field, member, parameter, type or branch the slice runs
     without, state that could be computed, a name that Simplest first bans, or brute force where a direct
     approach fits. Show the better version in a few lines. Report it only when it is smaller or clearly cleaner and
     does the same job, never to swap one reasonable approach for another.
6. Don't report style, taste, naming outside Simplest first, or ideas for making it more robust or general. Those
   lead to over-engineering. A sound slice gets no findings, and saying so is a good result.

Only read: `git diff`, `git log`, `git show`, `git status`, `git worktree list`. Never edit, build, run scripts,
commit or merge.

List findings numbered, each with `file:line`, what is wrong and the fix. End with exactly these two lines, which an
agent hook reads to record the review:

```text
WORKTREE: <the worktree's absolute path>
VERDICT: PASS
```

or `VERDICT: FINDINGS (<count>)` in place of `PASS`.
