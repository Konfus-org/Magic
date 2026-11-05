---
name: slice
description: Starts a slice. Use it on your own, without being asked, whenever the user asks for any change to code, docs or tools, and when starting the next PLAN.md slice. A listed slice starts after a short brief; anything else gets a proposal first.
---

# Slice

1. Read PLAN.md. Take the next unticked slice in the current bucket, or the one the user named. If the request isn't
   in PLAN.md, propose adding it and stop.
2. Check the size against [AGENTS.md](../../../AGENTS.md#slices). If it's too big, say so plainly, propose two to
   four smaller slices and stop until the user picks one.
3. Check it against the goal and decisions in PLAN.md. Flag drift, and flag any public API, file format, dependency,
   gem or architecture change as a decision that needs a yes.
4. Read only the code the slice needs, then write this brief:

   ```markdown
   ## Slice: <verb + what the user can see or run>
   Mode: spike | prototype | production
   Time-box: <one session, or less>
   -Change level: Docs | Code | Visual

   Goal: <one sentence>
   Serves: <the bucket goal it moves forward>

   In scope: <files or members>
   Out of scope: <the tempting next step, named>

   Works when: <a command, a sample and what it shows, or a test name>
   /goal (only when it fits): /goal <end state> - shown by AgentTaskComplete -Change <level> stamping the state and
   the slice-reviewer returning VERDICT: PASS - <what must not change> - or stop after <n> turns
   ```

   Include the `/goal` line only when the slice will take many turns toward an end state the transcript can show
   ([AGENTS.md](../../../AGENTS.md#slices)). Say in one sentence why it fits.

5. If the slice is listed in PLAN.md and needs no hard-to-reverse decision, state the brief in the chat and start
   building. Don't wait for approval: the user approved it when the bucket was sliced.
6. Otherwise, present the full proposal AGENTS.md asks for, led by the brief, in plan mode so it opens in
   plannotator. Make no edits until the user approves, trims or splits it.
