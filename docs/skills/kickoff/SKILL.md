---
name: kickoff
description: Interviews the user and splits a new effort into slices. Use it on your own, without being asked, whenever a request starts something too big for one slice, or the user wants to plan what comes next.
---

# Kickoff

1. Read the agent rules and the README, and skim the code the effort touches so the questions are concrete
   ([PROJECT.md](../PROJECT.md)).
2. Interview the user one question at a time:
   - What is this work for?
   - What one decision or outcome should it drive?
   - What does a user do first? What does "it works" look like?
   - What is the riskiest unknown? It is the first spike.
   - What is out of scope for now?
   Skip what the request already answers. If the answers describe several efforts, say so and ask which comes first.
3. Sum up the goal in three lines.
4. Propose the slices, riskiest first, each within the slice size limit and written with its mode and its runnable
   check, as `Verb + result (prototype; check: <command or sample>)`. Keep to the first five or so. Later work stays
   one line. Push back on any slice that polishes before it proves something.
5. If two or more of the slices don't depend on each other, use the [parallelize skill](../parallelize/SKILL.md)
   instead. Otherwise, tell the user a yes to the list lets each slice start without another
   approval, then wait for it.
