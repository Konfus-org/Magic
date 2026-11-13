---
name: tidy-worktrees
description: Removes finished worktrees, branches and sessions, and reports the rest. Use it on your own, without being asked, when a session starts and finished worktrees or stale claims exist, and after you delete a session.
---

# Tidy worktrees

1. Run `git worktree list` and `git branch --no-merged <merge target>`. Look at the sessions of the repository.
2. If the project has claims ([PROJECT.md](../PROJECT.md)), delete each claim whose worktree is gone.
3. Remove each worktree and branch for which these two conditions are true:
   - Its session is gone.
   - All its commits are merged into the merge target or into a remote branch.

   Run `git worktree remove`, then `git branch -d`. Do not use `-D`. For a branch that you made from a remote branch,
   set its upstream to that remote branch first. Then `-d` removes it.
4. Delete each session that is done. A session is done when these three conditions are true:
   - It does not run.
   - Its work merged, or its question has an answer.
   - Nothing in it waits for the user.

   Keep a session that was interrupted, that never started, or that waits for the user.
   - The user must approve each delete. A coordinator closes its own workers without a new approval
     ([parallelize skill](../parallelize/SKILL.md)).
   - Before you delete a session, detach each side session that it started and that still runs. Or wait until they
     stop.
   - After you delete a session, remove its worktree and branch as step 3 says, and delete its claim.
5. Report the rest in a few lines.

> **Warning:** Do not remove a worktree or a branch that has work that is not also somewhere else. Leave abandoned
> work where it is.
