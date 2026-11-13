---
name: retro
description: Turns what went wrong in the workflow into a few small edits to the agent rules or a skill. Use it on your own, without being asked, when a swarm finishes, a slice needed more than two rounds of review, the same fix failed twice, or the user corrects how you work. Also use it when the user asks for a retro.
---

# Retro

1. **Collect what happened.** Read the conversation and the git log. Find these facts:
   - What the user asked for, and what you built.
   - Where the turns went.
   - What the user corrected.
   - What the reviewer found more than one time.
2. **Find the cause in the docs.** Find the rule that let it happen. The rule is missing, not clear, hidden, or costs
   more than it saves. Delete or sharpen a rule before you add one.
3. **Propose three edits or fewer.** For each edit, give the file, the lines before and after, and what the edit
   would have prevented. Do not propose an edit about one task only, or about taste that the docs already cover.
4. When the user approves, make the edits as a slice at the lowest validation level ([slice skill](../slice/SKILL.md),
   [PROJECT.md](../PROJECT.md)).
