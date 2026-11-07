---
name: parallelize
description: Runs a large effort as a swarm, one agent per slice, each through the normal slice and finish flow. Use it on your own, without being asked, when the user hands over a list of several separate tasks, asks for something too big for a few slices (such as "make a game"), or a kickoff's slice list has two or more slices that don't depend on each other.
---

# Parallelize

You coordinate. You don't build: each slice is built by its own worker.

1. **Ask the overarching questions,** one at a time, skipping what the request already answers:
   - The kickoff questions ([kickoff skill](../kickoff/SKILL.md)): what it's for, the one outcome it drives, what
     "it works" looks like, the riskiest unknown, what is out of scope.
   - The decisions every slice will lean on: public API, file formats, new dependencies or gems, architecture. Get a
     yes on each now, so no worker has to stop for one.
   - How many reviews the user can take at once. Default 3.
2. **Split it into slices,** each within the size in [AGENTS.md](../../../AGENTS.md#slices), with its mode, its
   `-Change` level, its runnable check, the files or area it changes, and the slices it waits on. A list of tasks
   the user gave keeps their tasks; split or merge one only to fit the size, and say so.
3. **Propose the plan** in the chat: the goal in three lines, the decisions, and a table of slice, mode, check and
   what it waits on. A yes approves every slice in it. Wait for it.
4. **Start every slice that is ready** as a worker, up to the review count. A slice is ready once each slice it
   waits on has merged, or for a spike, answered. Never run two that change the same files or the same gem at once.
   Start with the riskiest spike alone, unless the user says the risk is known. Each worker runs in its own
   session: a side session in Claude Code, or whatever separate session or background agent the tool has. Give it
   the slice's brief in the slice skill's format, the merge target, the branch name and the decisions from step 1,
   and tell it to:
   - Follow the [slice skill](../slice/SKILL.md) from step 3, then the [finish skill](../finish/SKILL.md) through
     step 7: validation, a review in a fresh context, its summary, the user's review, and the merge once they
     approve. End there; the coordinator proposes what comes next. A spike ends after its summary, with its answer.
   - Merge one at a time: if the fast-forward fails because another worker merged first, rebase and run finish
     step 7 again.
   - Build only its slice, touch no other worker's worktree, and stop and report when blocked
     ([AGENTS.md](../../../AGENTS.md#when-blocked)).
5. **Track the swarm.** Keep a table in the chat of each slice, its worker, its worktree and its state: building,
   reviewed, waiting on the user, merged or blocked. `plannotator sessions --json` lists the reviews waiting on the
   user. Pass on a blocked worker's question to the user; never answer a design question for them.
6. **When a slice merges or a spike answers,** check the plan against what came of it. If it still holds, start
   the slices that are now ready (step 4). If it doesn't, stop starting workers and propose the change to the plan.
7. **When every slice has merged or answered,** run the [tidy-worktrees skill](../tidy-worktrees/SKILL.md),
   delete the workers' sessions with the user's approval, and sum up what landed, what was dropped and the next
   effort you propose.
