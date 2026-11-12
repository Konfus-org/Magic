---
name: retro
description: Turns what went wrong in the workflow into a few small edits to the agent rules or a skill. Use it on your own, without being asked, when a swarm finishes, a slice needed more than two rounds of review, the same fix failed twice, or the user corrects how you work; and whenever the user asks for a retro.
---

# Retro

1. **Gather what happened,** from the conversation and the git log: what was asked, what was built, where the turns
   went, what the user corrected, and what the reviewer found more than once.
2. **Find what in the docs let it happen:** a rule that is missing, unclear, ignored because it is buried, or costing
   more than it saves. Prefer deleting or sharpening a rule over adding one.
3. **Propose at most three edits,** each as the file, the lines before and after, and what it would have prevented.
   Leave out anything about one task only, or about taste the docs already cover.
4. With the user's yes, make them as a slice at the lowest validation level ([slice skill](../slice/SKILL.md),
   [PROJECT.md](../PROJECT.md)).
