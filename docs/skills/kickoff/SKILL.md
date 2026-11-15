---
name: kickoff
description: Interviews the user and splits a new effort into slices. Use it on your own, without being asked, each time a request starts something too big for one slice, or the user wants to plan what comes next.
---

# Kickoff

1. Read the agent rules and the README ([PROJECT.md](../PROJECT.md)). Look quickly at the code that the effort
   touches, so that your questions are specific.
2. Ask the user these questions, one at a time. Do not ask what the request already answers.
   - What is this work for?
   - What one decision or result must it give?
   - What does a user do first? What does "it works" look like?
   - What is the unknown with the most risk? It is the first spike.
   - What is out of scope for now?

   If the answers describe more than one effort, say so. Ask which effort comes first.
3. Write the goal in three lines.
4. Propose the slices, the one with the most risk first:
   - Each slice is within the slice size limit.
   - Write each slice as `Verb + result (prototype; check: <command or sample>)`, with its mode and its check.
   - Give about the first five slices in full. Write the later work in one line.
   - Push back on a slice that polishes before it proves something.
5. If two or more slices do not depend on each other, use the [parallelize skill](../parallelize/SKILL.md).
   Otherwise, tell the user these two things, and wait for the yes:
   - A yes to the list lets each slice start without a new approval.
   - The slices merge into one root branch. The root merges into the main checkout's branch one time, at the end,
     after the user's yes (Branches and merges, in [PROJECT.md](../PROJECT.md)).
6. After the yes, make the root branch `<type>/<effort>` from the main checkout's branch in its own worktree. Then
   start the first slice with the [slice skill](../slice/SKILL.md).
