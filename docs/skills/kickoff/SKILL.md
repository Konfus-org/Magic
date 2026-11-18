---
name: kickoff
description: Interviews the user and splits a new effort into slices. Use it on your own, without being asked, each time a request starts something too big for one slice, or the user wants to plan what comes next.
---

# Kickoff

1. In Claude Code, enter plan mode. Read [PROJECT.md](../PROJECT.md) and the README.
2. **Understand the problem.** Do this before you ask or plan anything. Do not skip it.
   - Write the problem in your own words: what is wrong or missing now, for whom, and how it shows.
   - Find what exists now in the code and the docs: the code that the effort touches, how it works, and a tool or
     feature that already does part of the job. When something is broken, see it fail, and find its cause with the
     [investigate skill](../investigate/SKILL.md).
   - Separate the cause from the symptom, and the need from the solution that the request names.
   - Write what you know with its evidence, what you assume, and what you do not know.
   - Put this in the plan file, in a section "The problem". If the facts show that the request aims at the wrong
     problem, show the user the evidence before you ask the questions.
3. Ask the user these questions together, with the multiple-choice prompt of the tool, four questions or fewer in
   each prompt. Do not ask them one at a time. Do not ask what the request already answers.
   - What is this work for?
   - What one decision or result must it give?
   - What does a user do first? What does "it works" look like?
   - What is the unknown with the most risk? It is the first spike. Start from the unknowns of step 2.
   - What is out of scope for now?

   If the answers describe more than one effort, say so. Ask which effort comes first.
4. Write the goal in three lines.
5. Propose the slices, the one with the most risk first:
   - Each slice is within the slice size limit.
   - Write each slice as `Verb + result (prototype; check: <command or sample>)`, with its mode and its check.
   - Give about the first five slices in full. Write the later work in one line.
   - Push back on a slice that polishes before it proves something.
6. If the user asks for a swarm, or four or more slices do not depend on each other, use the
   [parallelize skill](../parallelize/SKILL.md). Otherwise, write the problem, the goal and the slices in the plan
   file, tell the user these two things in it, and exit plan mode for the yes:
   - A yes to the list lets each slice start without a new approval.
   - The slices are commits on one root branch. The root merges into the main checkout's branch one time, at the end,
     after the user's yes (Branches and merges, in [PROJECT.md](../PROJECT.md)).
7. After the yes, make the root branch `<type>/<effort>` from the main checkout's branch in its own worktree. Then
   start the first slice with the [slice skill](../slice/SKILL.md).
