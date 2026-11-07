---
name: slice
description: Starts a slice. Use it on your own, without being asked, whenever the user asks for any change to code, docs or tools, and when starting the next agreed slice. A clear slice starts after a short brief; anything else gets a proposal first.
---

# Slice

1. Take the slice the user asked for, or the next one from a list they agreed to earlier in the conversation. If the
   request starts an effort too big for one slice, use the [kickoff skill](../kickoff/SKILL.md) instead.
2. Check the size against [AGENTS.md](../../../AGENTS.md#slices). If it's too big, say so plainly, propose two to
   four smaller slices and stop until the user picks one.
3. Check the request:
   - Its premises against the code and docs. If it's the wrong direction, say so with evidence before doing
     anything.
   - Against the goal and decisions agreed in the conversation. Flag drift.
   - If a current .NET or C# feature does the job better than what the request names, say so.
   - Before adding a component, dependency, file format or tool, look for an existing, well-maintained one and
     prefer it, even when the request names an approach.
   - Flag any public API, file format, dependency, gem or architecture change as a decision that needs a yes.
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

5. If the request is clear and the slice needs no hard-to-reverse decision, state the brief in the chat, make the
   worktree ([AGENTS.md](../../../AGENTS.md#worktrees)) and start building. Don't wait for approval: the request, or
   the user's yes to the slice list, is the approval.
6. Otherwise, present a proposal led by the brief, and make no edits until the user approves, trims or splits it:
   1. The approach.
   2. The smallest version that could work, the alternatives you considered, and why the chosen one is the
      simplest.
   3. For anything new, the existing libraries or tools you looked at.
   4. The riskiest assumption, what you haven't verified, and what would change your recommendation.
   5. For a sweeping change, before/after snippets and a data-flow diagram.

   In Claude Code, present it in plan mode so it opens in plannotator. Elsewhere, present it in the chat and wait.
7. When the slice is built, use the [finish skill](../finish/SKILL.md).
