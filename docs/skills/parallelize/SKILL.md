---
name: parallelize
description: Runs a large effort as a swarm: coders build slices side by side into a root branch, then QA, fixes, a cleanup and one review by the user. Use it on your own, without being asked, when the user hands over a list of several separate tasks, asks for something too big for a few slices (such as "make a game"), or a kickoff's slice list has two or more slices that don't depend on each other.
---

# Parallelize

The commands, paths and rules in this skill are in [PROJECT.md](../PROJECT.md).

You coordinate: you plan the slices, give them to workers, keep the user up to date and bring the whole in. You do
not build. After the user approves the plan, act like `/goal`: keep starting workers until the goal is met. Do not ask
the user whether to continue.

- Never offer the user a button, a choice or a question that starts work. If work serves the goal, start it. If it
  does not, put it in the notes of the plan (step 11).
- Only you ask the user, and only one item at a time (step 7).

The flow: coders build each slice and commit it. Merge trains build the slices into the root. When every slice is in,
a QA worker finds and ranks bugs, the user picks the fixes, a cleanup worker simplifies, and the user reviews the root
once.

**Each turn, do these steps in this order:**

- Read the state of each slice (step 5).
- For each worker that ended, act on its `SLICE:` line (step 6).
- Give the next profiling turn, and start the next merge train (step 6).
- If nothing waits on the user, give them the next item in the queue (step 7).
- Start each slice that is ready (step 4).
- If something changed, post an update (step 7).
- If every slice has merged or answered, go on to step 9, 10 or 11.
- If workers still run, end the turn and wait for them.

1. **Ask the overarching questions,** one at a time. Skip what the conversation already answers.
   - The kickoff questions ([kickoff skill](../kickoff/SKILL.md)): what it's for, the one outcome, what "it works"
     looks like, the riskiest unknown, and what is out of scope.
   - The decisions every slice leans on: public API, file formats, new dependencies or plugins, architecture.
   - How many workers can run at the same time. The default is 3.
   - The permission flags of the workers, if the user has not set them: `--permission-mode auto`, or
     `--allowedTools <list>`. Do not use plan mode.
2. **Split it into slices.** A coder writes and builds a slice in one run of one hour. Give each slice its mode, its
   validation level, its runnable check, its scope as the coder will claim it, and the slices it waits on. A list of
   tasks from the user keeps their tasks. Split a task only to fit the hour, and say so.
3. **Write the swarm plan,** and get the user's yes.
   1. Make the root branch, `<type>/<effort>`, from the merge target in its own worktree:
      `git worktree add <worktree folder>/<type>/<effort> -b <type>/<effort>`. Claim every slice's scope for it. Each
      slice branches from the root with `--track`, as `<type>/<effort>-<slice>`, never under the root's name.
   2. Write the plan to `agent-swarm/<effort>.md` in `git rev-parse --git-common-dir`. Only you write it. Rewrite it
      whenever something changes. It holds: the goal in three lines, the decisions, the root, the worker limit, the
      permission flags, the models (step 4), a table of each slice (branch, mode, check, scope, waits on, session ID,
      state), the queue for the user, the profiling queue, the merge queue, and the notes for the user (hacks, known
      issues, trade-offs, decisions you made, follow-ups you did not do).
   3. Present the plan. A yes approves every slice, each follow-up that serves the goal (step 8), and the close of
      each worker. With the plan, give this line to paste, with `<n>` eight times the number of slices, plus 20:
      `/goal every slice of the swarm has merged or answered and the final report is given, as the chat shows, or stop after <n> turns`
4. **Start every slice that is ready:** each slice it waits on has merged or answered, no unmerged slice changes the
   same files or module, and fewer workers run than the limit. Start with the riskiest spike alone, unless the user says
   the risk is known.

   Choose each worker's model by your own model. Use the same `--effort` as your own. If you do not know your
   effort, leave `--effort` out.

   | Your model | Coders and merge trains | QA and cleanup |
   | --- | --- | --- |
   | Fable 5 (`fable`) | Opus 5.5 (`opus`) | `fable` |
   | Opus 5.5 (`opus`) | Opus 5 (`claude-opus-5`) | `opus` |
   | Opus 5 (`claude-opus-5`) | Opus 4.8 (`claude-opus-4-8`) | `claude-opus-5` |
   | Any other | Your own model | Your own model |

   To start a worker, make a UUID for its session and write it in the plan. From the repository's root, in bash, run
   it in the background:
   `timeout -k 10 1h claude -p "<prompt>" --session-id <UUID> --model <model> --effort <effort> <permission flags> --output-format json`.
   To give it a message, run the same with `--resume <UUID> "<message>"` in place of `--session-id`. When it ends, its
   result comes to you as a new turn. In a tool without headless sessions, use a background agent with the same limit.

   A coder's prompt holds the slice's brief in the slice skill's format, its branch, the root, the decisions, and the
   other running slices with their scopes. It tells the coder to:
   - Make its worktree, `git worktree add --track -b <branch> <worktree folder>/<branch> <root>`, and claim its scope.
   - Follow the slice skill from step 3, and each skill that the work needs, such as investigate, cleanup,
     profiling and timeouts. Do not use the finish skill.
   - Build only. Run the build and stamp command. Do not run tests, headless runs, screenshots or reviews. Do not run
     the app, except to profile in a profiling turn.
   - When the slice builds, commit on its branch and end with `STATE: done`. For a spike, release its claim and end
     with `STATE: answered` after its summary.
   - Ask the user nothing. When a skill says to ask the user or to stop, end with `STATE: blocked`, the proposal, and
     each question as a multiple choice of two to four options, the recommended one first.
   - Before it profiles or measures, end with `STATE: needs-profiling`, with what it will run and for how long. When
     resumed with its turn, take it with the profiling turn command. If the take fails, end with
     `STATE: needs-profiling` again. Release the turn, and end with `STATE: profiling-done`
     and the results.
   - If the slice does not fit in the hour, end with `STATE: too-big`, what is done and what is left.
   - Touch no other worker's worktree.
   - End its last message with its follow-ups, its notes for the user, and this line:
     `SLICE: <branch>  STATE: <state>  CONTEXT: <percent>  NOTE: <what it did, its answer or its request>`.
5. **Read each slice's state from the plan and git, not from memory.** Copy each `SLICE:` line into the plan, because a
   merged branch is deleted. After a restart or a compaction, rebuild the picture from these.
6. **Act on each worker that ended,** by its state:
   - `done`: add it to the merge queue.
   - `merged` or `answered`: do step 8.
   - `rejected`, from a train: resume the coder with the reason. It rebases onto the root, fixes the build, commits
     and ends with `done`. `requeued`: add it to the merge queue again.
   - `blocked`: add its proposal and questions to the queue for the user. If the block is a failure whose cause is
     not proved, first start a spike that finds the cause. Give the user the choice of a fix only with that cause.
   - `needs-profiling`: add it to the profiling queue. `profiling-done`: end its turn, and resume it with "Continue
     your slice."
   - `validated`, from QA: resume it with "Continue." `qa-done`: do step 9.
   - A train, QA or cleanup worker that reached its time limit: resume it one time with "Continue." If it reaches the
     limit again, reset the train's slices that did not merge and make them `requeued`, or put QA's or the cleanup's
     state in the notes and go on.
   - A coder that ended with `too-big`, or reached the time limit: kill the processes it started ([timeouts skill](../timeouts/SKILL.md) step 5). If
     another worker's profiling turn ran during its run, resume it one time. Otherwise split the slice into slices
     that each fit in one hour, and start the first on the same branch and worktree.
   - No `SLICE:` line: if step 5 shows the slice merged, do step 8. If not, resume it one time with "Continue your
     slice." If it ends without the line again, treat it as too big.

   **Profiling turns.** Profiling is the only work that waits for a turn. Give the turns one at a time, in the order
   of the queue: resume the first coder with "Your profiling turn starts now." The turn ends when that coder ends. If
   it ended without its release, run the command that releases a profiling turn, in its worktree.

   **Merge trains.** When no train runs, and the merge queue has two or more slices or has one slice while no coder
   builds, start a train worker with up to five of them. It counts against the worker limit. Its prompt gives the
   root and the slices, and tells it that it owns their worktrees until it ends. It tells it to:
   1. Order the slices: fixes first, then features, then the others, each group in the order of the queue.
   2. Write down each slice's head commit. Rebase the first slice onto the root, and each next slice onto the one
      before. If a slice conflicts, abort the rebase, reset it with `git reset --keep <head>`, and leave it out as
      `rejected`.
   3. Run the build and stamp command in the last slice's worktree. If the build fails, run
      it in each slice's worktree from the first, and stop at the first that fails. That slice is `rejected`, with
      the error. The slices after it are `requeued`.
   4. In the root's worktree, run `git merge --ff-only <the last slice that built>`.
   5. For each slice that merged, release its claim, then run `git worktree remove` and `git branch -d`.
   6. Reset each slice that did not merge to its first head with `git reset --keep <head>`.
   7. End with one `SLICE:` line for each slice: `merged`, `rejected` with the reason, or `requeued`.
7. **Keep the user up to date.** Post one update when an item reaches the user, a slice merges or answers, or the plan
   changes, always in this shape. Send a notification where the tool can.

   ```text
   Waiting on you: QA's issues (2 more in the queue)
   In flight (2): search (building), export (profiling)
   Done (3/9): sign-in, settings page, file import
   Next: undo, once search merges
   ```

   **The queue for the user** holds the questions, QA's issues, the cleanup plan and the final review, in the order
   they come. Give only the first item. Give the next only when the user has finished it. Ask with the tool's
   multiple-choice prompt, at most four questions in one prompt, as the worker wrote them. Do not answer a design
   question for the user. When the user answers, resume the worker. Do not put a question in an update.
8. **When a slice merges or answers:** check the plan against what came of it. Add each follow-up that serves the
   goal as a slice, and put the others and the worker's notes in the plan. Do not add slices past two times the
   number that the user approved. Reorder or split slices without asking. Before you drop a slice, change a
   decision, or take a follow-up that needs a hard-to-reverse decision, add a question to the queue. If a ready slice
   is in the same area, you can resume the same coder with it, while it has built fewer than three slices and its
   `CONTEXT` is under half.
9. **QA,** when every slice has merged or answered:
   1. Start a QA worker in the root's worktree. Its prompt gives the root, the merge target and each slice's brief,
      and tells it to follow the [qa skill](../qa/SKILL.md). If it ends with `STATE: validated`, resume it with
      "Continue."
   2. Put its issues in the queue for the user as multiple-select questions, the most severe first, with each
      issue's rank, place and suggested fix.
   3. Add each issue that the user picks as a fix slice (step 4). Put the others in the notes.
   4. When the fixes merge, resume QA with "Check these fixes:" and their branches. Do this at most two times. Then
      put the issues left in the notes.
10. **Cleanup and the review,** when QA is done:
    1. Start a cleanup worker as a slice from the root. It follows the [cleanup skill](../cleanup/SKILL.md) on
       `git diff <merge target>...<root>`, and ends with `STATE: blocked` and its cleanup plan. Put the plan in the
       queue. When the user approves, resume it. It builds, commits and ends with `STATE: done`, like a coder.
    2. When the cleanup merges, have a worker rebase the root onto the merge target. If that conflicts, have it
       resolve the conflicts, and put the resolution in the queue. Then have it run
       the validate and stamp command in the root's worktree at the highest level of the slices, examine the
       screenshots, and get a review of the root in a fresh context ([review-slice skill](../review-slice/SKILL.md)).
       Its findings are fix slices.
    3. Put the user's review of the root in the queue: run the user's review command from the root's worktree, in the background, with the limit from the timeouts skill. Comments become fix slices.
    4. When fix slices from step 2 or 3 merge, do steps 2 and 3 again. Get a new review in a fresh context only when
       the fixes added more than about 100 lines.
    5. When the user approves, fast-forward the merge target to the root in its checkout. If the merge target moved,
       do step 2 again first. Release the root's claim, and remove its worktree and branch.
11. **The final report:** close each worker that still runs, run the [tidy-worktrees skill](../tidy-worktrees/SKILL.md),
    and give the user:
    - each slice, its commits and what it changed, one line each;
    - the hacks that stay, such as `// PROTO` code;
    - the known issues, the QA issues not fixed, and what nobody verified;
    - the trade-offs of the solution, and the alternatives;
    - the follow-ups not done, with the reason for each;
    - anything else the user must know, such as a decision you made for them.

    Do not offer to start any of these. Then delete the plan.
