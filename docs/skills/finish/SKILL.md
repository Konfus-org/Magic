---
name: finish
description: Validates, reviews, shows and merges a branch into the main checkout's branch, one time. The branch is a single slice, or the root branch of an effort when its last slice is in. Use it on your own, without being asked, as soon as the code of a single slice is written, when the last slice of an effort merges into its root, and again after you address the comments of the user's review. A slice of an effort does not use it.
---

# Finish

The commands and rules are in [PROJECT.md](../PROJECT.md). The branches and merges rule is a hard rule: this skill
merges into the main checkout's branch only at step 7, one time, after the user's yes.

This diagram shows the flow from validation to the merge.

```mermaid
flowchart TD
    Pull[Pull the main checkout's branch] --> Validate --> Review[Review in a fresh context] --> Summary --> UserReview[The user's review]
    Review -- findings --> Validate
    UserReview -- comments --> Validate
    UserReview -- yes --> Merge[One merge] --> Next[Propose the next work]
```

1. Pull and validate:
   1. Commit your work on the branch. Pull the main checkout's branch into it: `git merge <main checkout's branch>`.
      Resolve conflicts only in your changes. If a conflict needs a change to the work of another agent, stop and ask
      the user.
   2. Run the validate and stamp command at the level of the branch: for a root branch, the highest level of its
      slices.
   3. If a test fails, fix the cause. Do not make the test weaker, skip it or delete it.
2. For a change at the visual level, open each screenshot that the command printed and examine it. If the change shows
   only where the samples do not go, take a screenshot there with the screenshot command.
3. Get one review in a fresh context. Give the reviewer the worktree, the main checkout's branch and the slice, or the
   briefs of the slices of a root branch. Use the reviewer in PROJECT.md. If there is none, use a new session or
   subagent that follows the [review-slice skill](../review-slice/SKILL.md).
   - Skip this step for a small branch: 50 changed lines or fewer, or a change at the lowest validation level. Do not
     skip it when the branch changes a public API, a file format or a dependency.
   - Fix each finding at its cause. If you do not fix a finding, give the evidence. Then do step 1 again.
   - Get a new review only when the fixes add more than about 100 lines.
   - Run the reviewer in the foreground. A reviewer in the background records no review, and the merge is then
     refused.
4. Write the summary:
   - What changed and why. Give the reason for each hunk where it is not clear. Keep the fix and the cleanup apart.
     For a root branch, one line for each slice.
   - The level that you validated at, your assumptions, and what you did not check. For a visual change, what the
     screenshots show.
   - Each type, member, field and parameter that you added, and the caller that needs it. Remove each one that has no
     caller (Simplest first).
   - The next work that you propose, in one line.
5. Start the user's review. It asks the user if you can merge. From the worktree, run the user's review command with
   `--base <main checkout's branch>` in the background. Wait for it.
   - If it stops before the user decides, run it again. The comments of the user stay.
   - If the project has no review command, show `git diff --stat` and the important hunks in the chat. Ask "Can I
     merge `<branch>` into `<main checkout's branch>`?" Wait.
6. Address each comment. If you do not address a comment, give the evidence. Commit, validate again, and go back to
   step 4. If the user's review closes with no comments and no approval, ask the user what to do.
7. Approval in the user's review, or a yes in the chat, means merge. Do not ask again. If the yes is in the chat, stop
   the user's review. Then:
   1. If the main checkout's branch moved, do step 1 again. If no new work came in, the stamp command stops at once.
   2. In the main checkout, run the final merge command one time. Do not push.
   3. Release the claim. Then run `git worktree remove`. Then run `git branch -d`. Do not use `-D`.
   4. Remove each other worktree and branch that you made for the task in the same way. This includes the worktree
      that the app made for the session. For a branch that you made from a remote branch, set its upstream to that
      branch first. Then `-d` removes it.
   5. Delete the side sessions that you started and that are done
      ([tidy-worktrees skill](../tidy-worktrees/SKILL.md)).
8. Propose the next slice or effort in one line, and stop. Do not start it yourself.
