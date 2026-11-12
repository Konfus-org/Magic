---
name: slice
description: Starts a slice. Use it on your own, without being asked, whenever the user asks for any change to code, docs or tools, and when starting the next agreed slice. A clear slice starts after a short brief; anything else gets a proposal first.
---

# Slice

The rules and commands in this skill are in [PROJECT.md](../PROJECT.md).

1. Take the slice that the user asks for, or the next slice in a list that the user agreed to. If the request is too
   big for one slice, use the [kickoff skill](../kickoff/SKILL.md) instead.
2. Compare the size of the slice with the slice size limit. If the slice is too big,
   tell the user. Propose two to four smaller slices, and stop until the user selects one.
3. Examine the request:
   - Compare its premises with the code and the docs. If the direction is wrong, show the user the evidence before
     you do other work.
   - Compare it with the goal and the decisions that you agreed with the user. If it does not serve them, tell the
     user.
   - If a current language or framework feature does the job better than the method in the request, tell the user.
   - Before you add a component, dependency, file format or tool, look for a well-maintained one that exists. If you
     find one, prefer it, also when the request names a different method.
   - A change to a public API, a file format, a dependency, a plugin or the architecture is a decision. Get the user's
     yes for it.
4. Read only the code that the slice needs. Then write this brief:

   ```markdown
   ## Slice: <verb + what the user can see or run>
   Mode: spike | prototype | production
   Time-box: <one session, or less>
   Validation level: <level>

   Goal: <one sentence>
   Serves: <the bucket goal it moves forward>

   In scope: <the files and folders it changes, as it will claim them>
   Out of scope: <the tempting next step, named>

   Works when: <a command, a sample and what it shows, or a test name>
   /goal (only when it fits): /goal <end state> - shown by the validate and stamp command at <level> and the review
   in a fresh context returning VERDICT: PASS - <what must not change> - or stop after <n> turns
   ```

   Add the `/goal` line only for a slice that needs many turns to reach an end state in the transcript. Do not add it
   when the slice needs the user's decisions. Give the reason in one sentence. The user types the line; you cannot set
   it.
5. If the request is clear and the slice needs no hard-to-reverse decision:
   1. Write the brief in the chat. Do not wait for approval. The request, or the user's yes to the slice list, is the
      approval.
   2. Make the worktree. Then claim the scope of the brief (Worktrees and claims).
   3. If the claim reports an overlap, stop and ask the user.
   4. Build the slice.
6. If not, write a proposal that starts with the brief. Do not edit until the user approves, trims or splits it. The
   proposal gives:
   1. The approach.
   2. The smallest version that can work, the alternatives, and why the selected one is the simplest.
   3. For a new thing, the existing libraries and tools that you examined.
   4. The riskiest assumption, what you did not verify, and what can change your recommendation.
   5. For a sweeping change, snippets before and after the change, and a data-flow diagram.

   In Claude Code, write the proposal in plan mode. In other tools, write it in the chat
   and wait.
7. When the slice is built, use the [finish skill](../finish/SKILL.md).
