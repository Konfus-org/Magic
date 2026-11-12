---
name: simplify-review
description: Reviews one area of the code for what can be deleted, merged, flattened or made more elegant, and makes the findings into slices. Use it when the user asks to simplify, clean up or review an area, or when a retro or a slice summary names an area that keeps causing problems. It writes a review. It does not edit code.
---

# Simplify review

The rules, paths and commands are in [PROJECT.md](../PROJECT.md).

1. **Select one area** with the user, as the project defines an area. A larger request is more than one review.
2. **Read what you judge the area against:**
   - Simplest first and the code standards.
   - The root README, and the doc in `docs/` for the area.
   - For a rendering gem or the shader library, the `README.md` in its folder.

   Then read each file in the area, and how the rest of the code calls it.
3. **Look for less code that does the same job:**
   - Code that nothing calls.
   - A type that only forwards calls, or a helper with one caller.
   - A flag or a mode field.
   - State that the code can compute.
   - Two places that do the same thing.
   - An abstraction with one use.
   - A deep call chain or deep nesting.
   - Brute force where a better data shape removes the special cases.

   For each part, ask: "Is there a more elegant version?" The owner's taste comes first:
   - One longer method in sections with short comments, not many small methods.
   - The API shape that fits the call site.
   - No flags, and no helpers with one caller.
4. **Check each finding against the code** before you write it: what calls it, and what can break.
5. **Write the review** to `<reviews folder>/<Area>-<yyyy-MM-dd>.md`. Git does not track that folder. The review has
   these parts:
   1. **How to use it:** do one slice at a time. Read the index and the findings of that slice, not the whole file.
      Check each finding against the code first. Keep the behavior the same.
   2. **Decisions for the owner:** each finding that needs approval. Mark each one as "needs approval". A finding
      needs approval when it changes a public API, a file format, a dependency or the architecture. It also needs
      approval when it deletes something that a user can see.
   3. **The slice plan:** the findings in slices within the slice size limit, grouped by the files that they touch.
      Give each slice its branch, its net lines and its validation level.
   4. **The index** of the findings.
   5. **Each finding** as `### <AREA>-<n>: <title>`, with:
      - Its `file:line` and what is wrong.
      - The simpler version, in a few lines.
      - The lines that it removes, and its risk.
      - When it moves types, layers or calls: a Mermaid diagram of the architecture before and after. Do not draw a
        diagram with ASCII characters.
6. **Give it to the user:**
   - Give the total lines that it removes, and the decisions that wait for the owner.
   - Offer to run the slices that need no decision. Run them at the same time where their files do not overlap
     ([parallelize skill](../parallelize/SKILL.md)).
   - Each slice uses the [cleanup skill](../cleanup/SKILL.md).
