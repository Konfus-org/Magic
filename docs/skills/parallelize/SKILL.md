---
name: parallelize
description: Runs a large effort as a swarm, one agent per slice, each through the normal slice and finish flow. Use it on your own, without being asked, when the user hands over a list of several separate tasks, asks for something too big for a few slices (such as "make a game"), or a kickoff's slice list has two or more slices that don't depend on each other.
---

# Parallelize

You coordinate: you plan the slices, start a worker for each, keep the user up to date and bring the whole in. You
don't build.

1. **Ask the overarching questions,** one at a time, skipping what the conversation already answers:
   - The kickoff questions ([kickoff skill](../kickoff/SKILL.md)): what it's for, the one outcome it drives, what
     "it works" looks like, the riskiest unknown, what is out of scope.
   - The decisions every slice will lean on: public API, file formats, new dependencies or gems, architecture. Get a
     yes on each now, so no worker has to stop for one.
   - How many reviews the user can take at once. Default 3.
2. **Split it into slices,** each within the size in [AGENTS.md](../../../AGENTS.md#slices), with its mode, its
   `-Change` level, its runnable check, the files and folders it changes as its worker will claim them, and the
   slices it waits on. A list of tasks the user gave keeps their tasks; split or merge one only to fit the size, and
   say so.
3. **Choose where the slices merge.**
   - **One feature,** such as a new sample: a root branch, `feature/<effort>`, made from the merge target in a
     worktree of its own (`git worktree add ../Magic-worktrees/feature/<effort> -b feature/<effort>`) and claimed for
     every slice's scope together. Each worker branches from the root with `--track`, which makes the root its merge
     target, and is named beside it as `<type>/<effort>-<slice>`, never under it: git can't hold both
     `feature/new-sample` and `feature/new-sample/terrain`.
   - **Separate tasks:** no root. Each merges into the merge target.
4. **Write the plan** to `agent-swarm/<effort>.md` in the repository's git folder (`git rev-parse --git-common-dir`),
   which every worktree can read and nothing commits. It holds the goal in three lines, the decisions, the root
   branch if there is one, the review count, and a table of each slice's branch, mode, check, scope, what it waits on,
   and its state where git can't show it: answered, with the answer, or blocked, with the question. Follow-ups go at
   the end. Only you write it; rewrite it whenever something changes. Present it in the chat. A yes approves every
   slice in it. Wait for it.
5. **Start every slice that is ready** as a worker, up to the review count. A slice is ready once each slice it waits
   on has merged, or for a spike, answered, and no running slice's scope overlaps its own. Start with the riskiest
   spike alone, unless the user says the risk is known. Each worker runs in its own session: a side session in Claude
   Code, or whatever separate session or background agent the tool has. Give it the slice's brief in the slice
   skill's format, its branch, the branch it starts from, the decisions from step 1, and the other running slices
   with their scopes, and tell it to:
   - Make its worktree, with a root as `git worktree add --track -b <branch> ../Magic-worktrees/<branch> <root>`,
     and claim its scope ([AGENTS.md](../../../AGENTS.md#worktrees)).
   - Follow the [slice skill](../slice/SKILL.md) from step 3, then the [finish skill](../finish/SKILL.md) through
     step 7: validation, a review in a fresh context, its summary, the user's review, and the merge once they
     approve. A spike ends after its summary, with its answer.
   - Merge one at a time: if the fast-forward fails because another worker merged first, rebase and run finish
     step 7 again.
   - Build only its slice, touch no other worker's worktree, and stop and report when blocked
     ([AGENTS.md](../../../AGENTS.md#when-blocked)).
   - End its last message with one line:
     `SLICE: <branch>  STATE: merged | answered | blocked  NOTE: <what it did, its answer or its question>`.
6. **Read each slice's state from git, not from memory.** It has merged when `git branch --merged <root or merge
   target>` lists its branch, waits on the user's review when `plannotator sessions --json` lists its worktree, and is
   being built while it has a claim. Answered and blocked come from its worker's `SLICE:` line, which you copy into
   the plan. After a restart or a compaction, rebuild the picture from the plan and these.
7. **Keep the user up to date.** Post one update when a review starts waiting on them, a slice merges or answers, a
   worker is blocked, or the plan changes, and none in between, always in this shape:

   ```text
   Waiting on you (2): terrain review <url> · water asks: screen-space or planar reflections?
   In flight (2): foliage (building), sky (in review)
   Done (3/9): heightmap import, sample project, player spawn
   Next: wind, once foliage merges
   ```

   Put first the review that unblocks the most slices. Pass on a blocked worker's question as it is; never answer a
   design question for the user. Where the tool can send a notification, send one each time something new waits on
   the user.
8. **When a slice merges or answers,** check the plan against what came of it and add the worker's follow-ups to it.
   Reorder, or split a slice that grew too big, and report it in the next update. Adding or dropping a slice, or
   changing a decision from step 1, needs the user's yes; start no worker until they give it. Then start the slices
   that are now ready (step 5).
9. **When every slice has merged or answered:**
   1. With a root: in its worktree, rebase onto the merge target and run `tools/Scripts/AgentTaskComplete.ps1` at
      the highest level any slice used, looking at any screenshots. Have the root reviewed in a fresh context as one
      integration ([review-slice skill](../review-slice/SKILL.md)), and fix its findings as a slice of their own.
      Then fast-forward the merge target to the root, release the root's claim, and remove its worktree and branch as
      in finish step 7. Each slice was already approved, so this needs no review by the user, unless the rebase
      needed conflicts resolved: then show those and wait for their yes.
   2. Run the [tidy-worktrees skill](../tidy-worktrees/SKILL.md), delete the workers' sessions with the user's
      approval, and delete the plan.
   3. Sum up what landed, what was dropped, the follow-ups, and the next effort you propose.
