---
name: slice
description: Turns the next PLAN.md slice into a proposal before any edit. Use it on your own, without being asked, whenever the user asks for any change to code, docs or tools, and when starting the next slice.
---

# Slice

1. Read PLAN.md. Take the next unticked slice in the current bucket, or the one the user named. If the request isn't
   in PLAN.md, propose adding it and stop.
2. Check the size against [AGENTS.md](../../../AGENTS.md#slices). If it's too big, say so plainly, propose two to
   four smaller slices and stop until the user picks one.
3. Check it against the goal and decisions in PLAN.md. Flag drift, and flag any public API, file format, dependency,
   gem or architecture change as a decision that needs a yes.
4. Read only the code the slice needs, then present the proposal AGENTS.md asks for, led by this brief:

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

5. Present it in plan mode, so it opens in plannotator for the user's approval. Make no edits until they approve,
   trim or split it.
