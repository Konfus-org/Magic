---
name: tidy-worktrees
description: Removes finished worktrees, branches and sessions, and reports the rest. Use it on your own, without being asked, at the start of every session, and after deleting a session.
---

# Tidy worktrees

1. Run `git worktree list` and `git branch --no-merged <merge target>`, and look at the repository's sessions.
2. Look at the claims, if the project has them ([PROJECT.md](../PROJECT.md)). Delete each one whose worktree is gone.
3. Remove each worktree and branch whose session is gone and whose commits are all merged, into the merge target or
   a remote branch: `git worktree remove`, then `git branch -d` (never `-D`). A branch cut from a remote branch goes
   with `-d` once its upstream is set to that remote branch.
4. Delete the sessions that are done. A session is done when it isn't running, its work has merged or its question
   was answered, and nothing in it waits on the user. Keep any session that was interrupted, never started, or
   waits on the user.
   - The user approves each delete. A coordinator closes its own workers without a new approval
     ([parallelize skill](../parallelize/SKILL.md)).
   - Before deleting a session, detach the side sessions it started that are still going, or leave it until they
     finish.
   - After deleting a session, remove its leftover worktree and branch as in step 3, and delete its claim.
5. Report the rest in a few lines. Never remove a worktree or branch with work found nowhere else, and leave
   abandoned work where it is.
