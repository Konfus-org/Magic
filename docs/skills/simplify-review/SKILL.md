---
name: simplify-review
description: Reviews one area of the code for what can be deleted, merged, flattened or made more elegant, and turns the findings into slices. Use it when the user asks to simplify, clean up or review an area, or when a retro or a slice summary names an area that keeps getting in the way. It writes a review; it never edits code.
---

# Simplify review

The rules, paths and commands in this skill are in [PROJECT.md](../PROJECT.md).

1. **Pick one area** with the user, as the project defines one. Bigger asks are several reviews.
2. **Read what it is judged against:** Simplest first, the code standards, and every README from
   the repository root down to the area. Then read every file in the area, and how the rest of the code calls it.
3. **Look for less code that does the same job:** code nothing calls, a type that only forwards, a single-caller
   helper, a flag or mode field, state that could be computed, two places doing one thing, an abstraction with one
   use, a deep call chain or nesting, and brute force where a better data shape makes the cases disappear. Ask of
   each part: is there a more elegant version? The owner's taste comes first: one longer method split into sections
   by short comments over many small methods, the API shape that fits the call
   site, and no flags or single-caller helpers.
4. **Check each finding against the code** before writing it down: what calls it, and what would break.
5. **Write the review** to `<reviews folder>/<Area>-<yyyy-MM-dd>.md`, which git doesn't track:
   - How to work through it: one slice at a time; read the index and that slice's findings, not the whole file;
     check each finding against the code first; keep behaviour the same.
   - Decisions for the owner: each finding that changes public API, a file format, a dependency or the architecture,
     or deletes something a user could see, marked as needing approval.
   - A slice plan: findings grouped into slices within the slice size limit, by the files they touch, with
     each slice's branch, net lines and validation level.
   - An index of the findings, then each finding as `### <AREA>-<n>: <title>`, with its `file:line`, what is wrong,
     the simpler version in a few lines, the lines it saves, and its risk. When it moves types, layers or calls, add a
     Mermaid graph of the architecture before and after. Do not draw a graph with ASCII characters.
6. **Hand it over:** sum up the total lines it saves and the decisions waiting on the owner. Offer to run the slices
   that need no decision, side by side where their files don't overlap ([parallelize skill](../parallelize/SKILL.md)).
   Each slice runs with the [cleanup skill](../cleanup/SKILL.md).
