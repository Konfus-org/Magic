---
name: finish
description: Validates, reviews, presents and merges a built slice. Use it on your own, without being asked, as soon as a slice's code is written, and again after the user's review comments are addressed.
---

# Finish

1. Run `git rebase --autostash <merge target>`, then `tools/Scripts/AgentTaskComplete.ps1 -Change <level>`. If
   putting your work back conflicts, resolve it in your own changes and drop the stash. Fix a failing test at its
   cause; never loosen, skip or delete it.
2. For a `Visual` change, open and look at every screenshot it printed. If the change only shows somewhere the
   samples don't reach, screenshot that too with `tools/Scripts/Screenshot.ps1 -Project` or `-Commands`.
3. Have the validated state reviewed in a fresh context, naming the worktree, the merge target and the slice: in
   Claude Code, the `slice-reviewer` subagent; elsewhere, a new session or subagent that follows the
   [review-slice skill](../review-slice/SKILL.md). Fix its findings at their cause, or say with evidence why not,
   then run step 1 again. Another review is needed only once fixes add more than about 100 lines.
4. Summarize:
   - What changed and why, hunk by hunk where the reason isn't obvious, with the fix and the cleanup kept apart.
   - The level you validated at, what you assumed and what you couldn't verify. For a `Visual` change, what the
     screenshots show.
   - Each type, member, field and parameter you added, with the caller in this slice that needs it. Remove any that
     has none ([Simplest First](../../../AGENTS.md#simplest-first)).
   - The next slice you propose, in one line.
5. From the worktree, run `plannotator review --base <merge target>` in the background and wait for it. If it stops
   before the user decides, run it again; their comments are kept. The page shows the worktree's branch, which is
   the session's title. `plannotator sessions --json` lists each open review's URL and worktree. Without
   plannotator, show `git diff --stat` and the key hunks in the chat and wait for the user.
6. Address each comment, or say with evidence why not, validate again, and go back to step 4. If the review closes
   with neither comments nor approval, ask what the user wants.
7. Approval, in the review or typed as "approved", means merge. Don't ask again. An approval typed in the chat
   leaves the review open: stop it. Then:
   1. Commit on the worktree branch ([Commits](../../../AGENTS.md#commits)).
   2. Rebase onto the merge target and run `tools/Scripts/AgentTaskComplete.ps1` again at the same level. If
      nothing landed it returns at once; if it validates again, look at a `Visual` change's new screenshots.
      Resolve conflicts in your own changes only; if one needs another agent's work changed, stop and ask.
   3. Fast-forward the merge target to your branch. Push nothing.
   4. `git worktree remove`, then `git branch -d` (never `-D`).
   5. Remove every other worktree and branch made for the task the same way, including the one the app made for
      the session. A branch cut from a remote branch goes with `-d` once its upstream is set to that remote branch.
   6. Delete the side sessions you started that are done ([tidy-worktrees skill](../tidy-worktrees/SKILL.md)).
8. Stop. Propose the next slice, suggest the user start it fresh with `/clear`, and give its brief in one or two
   lines to paste. Never start it on your own.
