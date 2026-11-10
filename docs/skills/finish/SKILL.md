---
name: finish
description: Validates, reviews, presents and merges a built slice. Use it on your own, without being asked, as soon as a slice's code is written, and again after the user's review comments are addressed.
---

# Finish

1. Rebase and validate:
   1. Run `git rebase --autostash <merge target>`. If the stash conflicts when it comes back, resolve the conflict in
      your changes and drop the stash.
   2. Run `tools/Scripts/AgentTaskComplete.ps1 -Change <level>`.
   3. If a test fails, fix the cause. Do not loosen, skip or delete the test.
2. For a `Visual` change, open each screenshot that the script printed and examine it. If the change shows only where
   the samples do not go, take a screenshot there with `tools/Scripts/Screenshot.ps1 -Project` or `-Commands`.
3. Get a review in a fresh context. Give the reviewer the worktree, the merge target and the slice. In Claude Code,
   use the `slice-reviewer` subagent. In other tools, use a new session or subagent that follows the
   [review-slice skill](../review-slice/SKILL.md).
   - Fix each finding at its cause, or give the evidence for not fixing it. Then do step 1 again.
   - Get a new review only when the fixes add more than approximately 100 lines.
4. Write the summary:
   - What changed and why. Give the reason for each hunk where it is not clear. Keep the fix and the cleanup apart.
   - The level that you validated at, your assumptions, and what you did not verify. For a `Visual` change, what the
     screenshots show.
   - Each type, member, field and parameter that you added, and the caller in this slice that needs it. Remove each
     one that has no caller ([Simplest First](../../../AGENTS.md#simplest-first)).
   - The next slice that you propose, in one line.
5. Start the user's review. From the worktree, run `plannotator review --base <merge target>` in the background. Wait
   for it.
   - If it ends before the user decides, run it again. The user's comments stay.
   - The page shows the branch of the worktree, which is the title of the session. `plannotator sessions --json`
     lists the URL and the worktree of each open review.
   - If plannotator is not available, show `git diff --stat` and the important hunks in the chat, and wait.
6. Address each comment, or give the evidence for not addressing it. Validate again, and go back to step 4. If the
   user's review closes with no comments and no approval, ask the user what to do.
7. Approval in the user's review, or "approved" in the chat, means merge. Do not ask again. If the approval is in
   the chat, stop the user's review. Then:
   1. Commit on the branch of the worktree ([Commits](../../../AGENTS.md#commits)).
   2. Rebase onto the merge target, and run `tools/Scripts/AgentTaskComplete.ps1` again at the same level. If no new
      work landed, it returns immediately. If it validates again, examine the new screenshots of a `Visual` change.
   3. Resolve conflicts only in your changes. If a conflict needs a change to the work of another agent, stop and ask
      the user.
   4. Fast-forward the merge target to your branch. Do this in the checkout of the merge target: the main checkout,
      or the root worktree of a swarm. Do not push.
   5. Release the claim with `tools/Scripts/Claim.ps1 -Release`. Then run `git worktree remove`. Then run
      `git branch -d`. Do not use `-D`.
   6. Remove each other worktree and branch that you made for the task in the same way. This includes the worktree
      that the app made for the session. For a branch that you made from a remote branch, set its upstream to that
      branch first. Then `-d` removes it.
   7. Delete the side sessions that you started and that are done ([tidy-worktrees skill](../tidy-worktrees/SKILL.md)).
8. Stop.
   - A worker in a swarm ends with its `SLICE:` line ([parallelize skill](../parallelize/SKILL.md)). The coordinator
     starts the next work.
   - In other cases, propose the next slice. Suggest that the user starts it with a clear context (`/clear`). Give its
     brief in one or two lines to paste. Do not start it yourself.
