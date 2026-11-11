---
name: parallelize
description: Runs a large effort as a swarm of workers that each build one slice at a time through the normal slice and finish flow. Use it on your own, without being asked, when the user hands over a list of several separate tasks, asks for something too big for a few slices (such as "make a game"), or a kickoff's slice list has two or more slices that don't depend on each other.
---

# Parallelize

You coordinate: you plan the slices, give each slice to a worker, keep the user up to date and bring the whole in. You
do not build. After the user approves the plan, act like `/goal`: keep starting workers until the goal is met. Do not
ask the user whether to continue.

- Never offer the user a button, a choice or a question that starts work. If work serves the goal, start it. If it
  does not, put it in the notes of the plan, which you give at the end (step 10).
- Only you ask the user, and only one thing at a time (step 8). The workers still plan with the user through you, and
  the user still reviews each change.

**Each turn, do these steps in this order:**

- Read the state of each slice (step 6).
- For each worker that ended, act on its `SLICE:` line (step 7).
- For each review or answer that came back from the user, resume its worker (step 8).
- Give the next profiling turn, and start the next merge train (step 7).
- If nothing waits on the user, give them the next item in the queue (step 8).
- Start each slice that is ready (step 5).
- If something changed, post an update (step 8).
- If every slice has merged or answered, do step 10.
- If workers still run, end the turn and wait for them.

1. **Ask the overarching questions,** one at a time. Skip what the conversation already answers.
   - The kickoff questions ([kickoff skill](../kickoff/SKILL.md)): what it's for, the one outcome it drives, what
     "it works" looks like, the riskiest unknown, and what is out of scope.
   - The decisions every slice will lean on: public API, file formats, new dependencies or gems, architecture. Get a
     yes on each now, so no worker has to stop for one.
   - How many workers can run at the same time. The default is 3.
   - The permission flags of the workers, if the user has not set them: `--permission-mode auto`, or
     `--allowedTools <list>`. Do not use plan mode.
2. **Split it into slices.** Each slice must fit in one run of a worker. In one hour, the worker builds the slice,
   validates it and gets its review in a fresh context. Keep the slice's validation level small enough for that hour.
   Give each slice its mode, its `-Change` level, its runnable check, the files and folders
   it changes as its worker will claim them, and the slices it waits on. A list of tasks from the user keeps their
   tasks. Split a task only to fit the hour, and say so.
3. **Choose where the slices merge.**
   - **One feature,** such as a new sample: a root branch, `feature/<effort>`, made from the merge target in a
     worktree of its own (`git worktree add ../Magic-worktrees/feature/<effort> -b feature/<effort>`) and claimed for
     every slice's scope together. Each worker branches from the root with `--track`, which makes the root its merge
     target. Name the worker's branch beside the root as `<type>/<effort>-<slice>`, never under it: git can't hold
     both `feature/new-sample` and `feature/new-sample/terrain`.
   - **Separate tasks:** no root. Each slice merges into the merge target.
4. **Write the swarm plan** to `agent-swarm/<effort>.md` in the repository's git folder
   (`git rev-parse --git-common-dir`), which every worktree can read and nothing commits. Only you write it. Rewrite it
   whenever something changes. It holds:
   - the goal in three lines, the decisions, the root branch if there is one, the worker limit and the permission
     flags;
   - a table of each slice's branch, mode, check, scope, the slices it waits on, its worker's session ID and its state;
   - the queue for the user (step 8), the queue of profiling turns and the merge queue (step 7);
   - the notes for the user: hacks, known issues, trade-offs, decisions that you made for the user, and follow-ups
     that you did not do.

   Present the plan in the chat. A yes approves every slice in it, each follow-up that serves the goal (step 9), and
   the close of each worker. Wait for the yes. With the plan, give the user this line to paste. It keeps the session
   going until the end. Make `<n>` eight times the number of slices, plus 10.
   `/goal every slice of the swarm has merged or answered and the final report is given, as the chat shows, or stop
   after <n> turns`
5. **Start every slice that is ready.** A slice is ready when all of these are true:
   - each slice that it waits on has merged, or for a spike, answered;
   - no unmerged slice changes the same files or the same gem;
   - fewer workers run than the worker limit.

   Start with the riskiest spike alone, unless the user says that the risk is known. To start a worker:
   1. Make a new UUID for its session, and write it in the plan.
   2. From the repository's root, start the worker in the background, in bash, with a time limit of one hour:
      `timeout -k 10 1h claude -p "<prompt>" --session-id <UUID> <permission flags> --output-format json`.
   3. When the worker ends, its result comes to you as a new turn.

   To give a worker a message, resume it with the same limit:
   `timeout -k 10 1h claude -p --resume <UUID> "<message>" <permission flags> --output-format json`. In a tool that
   cannot start a headless session, use a separate session or a background agent with the same limit.

   The prompt holds the slice's brief in the slice skill's format, its branch, the branch it starts from, the
   decisions from step 1, and the other running slices with their scopes. It tells the worker to:
   - Make its worktree, with a root as `git worktree add --track -b <branch> ../Magic-worktrees/<branch> <root>`,
     and claim its scope ([AGENTS.md](../../../AGENTS.md#worktrees)).
   - For a spike, follow the [slice skill](../slice/SKILL.md) from step 3. Release its claim
     (`tools/Scripts/Claim.ps1 -Release`), and end after its summary with `STATE: answered`.
   - For other slices, follow the slice skill from step 3, then the [finish skill](../finish/SKILL.md) steps 1 to 4:
     validation, a review in a fresh context, and its summary. Then end with `STATE: ready-for-review`. The coordinator
     starts the user's review, not the worker.
   - When it gets the user's comments, address them, do finish steps 1 to 4 again, and end with
     `STATE: ready-for-review`.
   - When it gets the user's approval, commit on its branch ([Commits](../../../AGENTS.md#commits)), and end with
     `STATE: approved`. A merge train merges it (step 7).
   - Build only its slice, and touch no other worker's worktree ([AGENTS.md](../../../AGENTS.md#when-blocked)).
   - Ask the user nothing, because nobody sees the run. When a skill or AGENTS.md says to ask the user, or to stop
     and report, end with `STATE: blocked`. This includes a proposal or a decision (slice step 6). Put the proposal in
     the message. Write each question as a multiple choice: two to four options, with the recommended option first.
   - Before it profiles or measures CPU, RAM or GPU use, or runs anything that other runs of Magic can change, end
     with `STATE: needs-profiling`. Say what it will run and for how long. Coding, builds, tests and validation need no
     profiling turn.
   - When it gets its profiling turn, take it with `tools/Scripts/Profiling.ps1 -Minutes <n>`, at most 20 minutes.
     If the take fails, end with `STATE: needs-profiling` again. If it succeeds, profile. Then release the turn with
     `-Release`. End with `STATE: profiling-done` and the results.
   - If its work does not fit in the hour, end with `STATE: too-big`, what is done and what is left.
   - List its follow-ups and its notes for the user in its last message: hacks, known issues and trade-offs. Then end
     the message with this line, and give its context use if it can see it:
     `SLICE: <branch>  STATE: <state>  CONTEXT: <percent>  NOTE: <what it did, its answer or its request>`. The state
     is one of `ready-for-review`, `approved`, `answered`, `blocked`, `needs-profiling`, `profiling-done` and
     `too-big`. A merge train gives `merged`, `rejected` or `requeued` for each of its slices.
6. **Read each slice's state from the plan, git and plannotator, not from memory.** The worker's `SLICE:` line gives
   merged, answered, blocked and too big; copy it into the plan, because a merged branch is deleted. A slice is in the
   user's review when `plannotator sessions --json` lists its worktree. It runs while its worker's command runs. After
   a restart or a compaction, rebuild the picture from these.
7. **Act on each worker that ended,** by the state on its `SLICE:` line:
   - `ready-for-review`: add its review to the queue for the user (step 8).
   - `approved`: add it to the merge queue.
   - `merged` or `answered`: do step 9.
   - `requeued`, from a merge train: add the slice to the merge queue again.
   - `rejected`, from a merge train: resume the slice's worker with the reason. The worker rebases onto the merge
     target and runs `tools/Scripts/AgentTaskComplete.ps1`. If that passes with no other change, it ends with
     `approved` again. After any other change, it ends with `ready-for-review`.
   - `blocked`: add its proposal and its questions to the queue for the user (step 8).
   - `needs-profiling`: add it to the queue of profiling turns.
   - `profiling-done`: end its profiling turn, and resume the worker with "Continue your slice."
   - A train worker that reached its time limit: resume it one time with "Continue the train." If it reaches the
     limit again, give each of its slices that has not merged the state `requeued`.
   - `too-big`, or the worker reached its time limit:
     1. Kill each process that the worker started ([timeouts skill](../timeouts/SKILL.md) step 5).
     2. If a profiling turn of another worker ran during its run, resume it one time with "Continue your slice."
        Do not split the slice for that run.
     3. If step 6 shows that the slice merged, do step 9.
     4. If not, split the slice into smaller slices that each fit in one hour. Record the split in the plan.
     5. Start the first new slice on the same branch and worktree. Do not ask the user.
   - No `SLICE:` line: read the slice's state (step 6). If it merged, do step 9. If not, resume the worker one time
     with "Continue your slice." If it ends without the line again, treat it as too big.

   **Profiling turns.** One worker at a time profiles. The lock in the scripts keeps the others out of its way:
   while a turn runs, builds, tests and runs of Magic from other worktrees wait (`tools/Scripts/Profiling.ps1`). The
   other workers keep working.
   1. Give the turns one at a time, in the order of the queue. Resume the first worker in the queue with "Your
      profiling turn starts now."
   2. The turn ends when that worker ends, in any state. Remove it from the queue. If it still needs a turn, it ends
      with `STATE: needs-profiling` again and goes to the back of the queue.
   3. If the worker ended without its release, run `<its worktree>/tools/Scripts/Profiling.ps1 -Release`. The copy in
      another worktree releases nothing. A turn also ends by itself at its time.

   **Merge trains.** Approved slices merge in trains, so that many slices cost one validation.
   1. Start a train when the merge queue has slices and no train runs. A train worker counts against the worker limit.
      Give it up to five slices from the queue. Slices that come later wait for the next train.
   2. Start the train worker as in step 5, with its own prompt. The prompt gives the merge target, the slices, and the
      `-Change` level of each. It tells the worker that it owns the worktrees of these slices until it ends, because
      their workers have ended. It tells the worker to do one validation in each run, and to:
      - Order the slices: fixes first, then features, then the others, such as cleanups and refactors. In each group,
        keep the order of approval.
      - Write down the commit at the head of each slice before it changes anything.
      - Stack the slices in their worktrees. Rebase the first slice onto the merge target. Rebase each next slice onto
        the slice before it. If a slice conflicts, abort its rebase. Reset it to its first head
        (`git reset --keep <head>`), and leave it out as `rejected`.
      - Run `tools/Scripts/AgentTaskComplete.ps1` in the worktree of the last slice, at the highest level of the
        slices. When the merge target is a root branch, use `Docs`: the root gets its full validation in step 10.
        For a `Visual` level, open each screenshot and examine it before the validation counts as a pass.
      - If the validation fails, find the longest run of slices from the first that passes. Each slice's worktree
        holds the merge target and the slices up to it. Validate those worktrees, and halve the run each time.
      - Fast-forward the merge target to the last slice that passed, in the checkout of the merge target. If the
        fast-forward fails because the merge target moved, stack the slices again on it, and validate again.
      - For each slice that merged, release its claim. Then remove its worktree and its branch (finish step 7).
      - Reset each slice that did not merge to its first head. The first slice that failed is `rejected`, with the
        reason. The slices after it are `requeued`.
      - End with one `SLICE:` line for each slice: `merged`, `rejected` with the reason, or `requeued`.
8. **Keep the user up to date.** Post one update when an item reaches the user, a slice merges or answers, or the plan
   changes, and none in between, always in this shape:

   ```text
   Waiting on you: the review of terrain <url> (2 more in the queue)
   In flight (2): foliage (building), sky (profiling)
   Done (3/9): heightmap import, sample project, player spawn
   Next: wind, once foliage merges
   ```

   Where the tool can send a notification, send one each time something new reaches the user.

   **The queue for the user.** Only you ask the user, and only one item at a time, so that the user misses nothing. The
   queue holds the reviews and the questions, yours too, in the order that they come.
   1. Give the user only the first item. Give the next item only when the user has finished the first.
   2. For a review, run `plannotator review --base <merge target>` from the worker's worktree, in the background. Give
      it the limit from the [timeouts skill](../timeouts/SKILL.md).
      - If the user approves in the chat, stop the open review.
      - If it reaches its limit, start it again (timeouts step 4).
      - If it ends with comments or an approval, resume the worker with them.
      - If it ends with no decision, ask the user a multiple-choice question: review the slice again, or drop it.
   3. For questions, give the worker's proposal first, if it has one. Then ask its questions with the tool's
      multiple-choice prompt, at most four in one prompt, as the worker wrote them. In a tool without such a prompt,
      write each question with numbered options. Do not answer a design question for the user. When the user has
      answered all of them, resume the worker with the answers.
   4. Do not put a question in an update. The update tells only how many items wait.
9. **When a slice merges or answers:**
   1. Check the plan against what came of it.
   2. If the plan has more than two times the number of slices that the user approved, add no more slices. Put the
      remaining follow-ups in the notes.
   3. Read the worker's follow-ups and notes. Add each follow-up that serves the goal as a slice. Copy the notes into
      the plan, and put the other follow-ups there too:
      - polish of `spike` or `prototype` code that nothing depends on yet ([Modes](../../../AGENTS.md#modes));
      - work that is out of scope.
   4. Reorder or split slices, and report it in the next update. Do not ask the user first.
   5. Before you drop a slice or change a decision from step 1, add a question to the queue for the user (step 8).
      Keep the other workers busy until the user answers.
   6. If a follow-up needs a hard-to-reverse decision, add the decision to the queue as a multiple-choice question.
      When the user answers, add the slice, or put the follow-up in the notes.
   7. If a ready slice is in the same area as the worker's last slice, you can give it to the same worker. Do this only
      while the worker has built fewer than three slices and its context is less than half full. Resume the worker
      with the slice's brief (step 5). If the worker gave no `CONTEXT`, start a new worker for the slice.

   A worker that ended needs nothing more to close. To close a worker that still runs, stop its background command.
   In a tool without headless sessions, end the worker's session and delete it.
10. **When every slice has merged or answered:**
    1. With a root, bring it in. Each slice was approved already, so the root needs no review by the user.
       1. Have a worker rebase the root onto the merge target, in the root's worktree.
       2. If the rebase has conflicts, have the worker resolve them. Add the resolution to the queue for the user
          (step 8), and wait for the answer.
       3. Have the worker run `tools/Scripts/AgentTaskComplete.ps1` at the highest level that any slice used. Examine
          the screenshots.
       4. Get a review of the root as one integration in a fresh context
          ([review-slice skill](../review-slice/SKILL.md)). Fix its findings as a slice of their own.
       5. Fast-forward the merge target to the root. Release the root's claim. Remove its worktree and branch as in
          finish step 7.
    2. Close each worker that still runs, and run the [tidy-worktrees skill](../tidy-worktrees/SKILL.md).
    3. Give the user the final report:
       - A summary of each change: each slice, its commits and what it changed, in one line each.
       - The hacks that stay, such as `// PROTO` code.
       - The known issues, and what nobody verified.
       - The trade-offs of the current solution, and the alternatives.
       - The follow-ups that you did not do, with the reason for each.
       - Anything else that the user must know, such as a decision that you made for them.

       Do not offer to start any of these. The user decides.
    4. Delete the plan.
