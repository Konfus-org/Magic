---
name: cleanup
description: Makes code smaller, simpler and clearer without a change to what it does. It has three uses - the branch pass that the finish skill runs on each branch before QA, a cleanup of code that the user names, and a review of one area that makes its findings into slices. Use it on your own, without being asked, before each branch goes to QA, and each time the user asks to clean up, refactor, simplify, tidy, untangle, improve or review code or an area, or says that code feels messy, big or hard to follow.
---

# Cleanup

A cleanup makes code smaller, simpler and clearer. It does not change what the code does. The best cleanup removes
more code than it adds. The rules, paths and commands are in [PROJECT.md](../PROJECT.md).

| Use | When | Edits |
| --- | --- | --- |
| Branch pass | The finish skill, before QA, on each branch | Yes, without a plan |
| Target | The user names code to clean up | Yes, after the user approves the plan |
| Area review | The user asks to simplify or review an area, or the target is too big for one slice | No. It writes a review |

## Good code

Good code here has these qualities:

- **Less code.** Remove before you add.
- **The most elegant solution:** a better data shape, or a different frame that removes the special cases. Many
  special cases are a sign to stop and think again.
- **As simple as possible.**
- **No repeated code.** Merge things that are alike. Use what exists.
- **Few layers and few abstractions.** The code reads from top to bottom. Go-to-definition and a stack trace go
  directly to it. Code that is easy to read is easy to debug.
- **Few methods.** A method is for reuse. Keep one flow in one long method, in sections with short comments. Do not
  split it into methods that each have one caller.
- **Names that say what a thing is and does.** A long name is good when it is clear. Use the words that the code
  already uses (Naming).
- **A simple structure:** each thing is where a reader looks for it first.
- **The best practices of the language and its framework,** as the code standards apply them. Use a current language
  or library feature where it makes the code simpler.

## The changes

Look for these changes, in this order. For each part, ask: "Is there a more elegant version?"

1. Remove what nothing needs:
   - Code that nothing calls, and members, parameters and overloads without a caller.
   - Flags and mode fields, and state that you can compute.
   - Guards for cases that cannot occur.
2. Merge what repeats. Use a method, type or library feature that exists in place of new code.
3. Remove layers: types that only forward calls, interfaces with one implementation, and helpers with one caller.
4. Put a method with one caller back into its caller, as a section with a short comment. When you put a method into
   a constructor, build the result. An analyzer can then report an error in the code that calls the constructor.
5. Find a better data shape or a different frame that removes the special cases.
6. Make deep nesting flat with guard clauses and early returns.
7. Use the API shape that fits each call site (API shape).
8. Make the names and the folders say what each thing is and does.
9. Fix what breaks a best practice of the language or its framework.

Do not add a new abstraction, base type, option, setting or general system, or anything for a later need. If the
simple version does not fit, stop and tell the user why. Check each change against the code before you make or
write it: what calls it, and what can break.

## Branch pass

1. Read `git diff <merge target>...<branch>`. The scope is the lines that the branch adds or changes, the members
   that it changes, and what they call directly. For a `prototype` slice, the scope is only the lines that the slice
   writes. A spike does not merge, so it gets no pass.
2. Make the changes above in that scope. Also remove what the branch made unnecessary, and each member that it added
   without a caller. Do not change what the code does.
3. A change that grows past a few lines, reaches a file that the branch does not touch, or needs a decision is not
   part of the pass. Write it in the summary as a proposed follow-up.
4. Build, and run the branch's check. Commit with the subject of the branch's type. In the summary, keep the cleanup
   hunks apart from the fix.

## Target

1. **Find the target.** Name the files and members that the user means. If the target is too big for one slice, do
   an area review first. Then do each of its slices as a target.
2. **Read before you change.** Read the target, its callers and the doc in `docs/` for its area, such as
   `docs/RENDERING.md` for a rendering gem or `docs/SHADERS.md` for the shader library. Write down what the target
   does now, and the check that shows it.
3. **Find the changes** above.
4. **Write the cleanup plan** in plan mode, as the [slice skill](../slice/SKILL.md) (step 6) says. Get the user's yes
   before you edit. The plan has:
   - Each change in one line, with its reason.
   - Code before and after each important change, short enough to read quickly.
   - When the change moves types, layers or calls: a Mermaid diagram of the architecture before and after. Draw it
     as the [ste-docs skill](../ste-docs/SKILL.md), section 7, says.
   - An estimate of the lines that it removes.
   - The decisions that need the user's yes. These are a change to a public API, a file format or a plugin
     interface, and the removal of a feature that a user can see.
   - If the target is `spike` or `prototype` code that nothing depends on yet, a note that says so (Modes).
5. **Do the plan, and keep the behavior the same.**
   - Validate at the lowest level that covers the target. Use the visual level when the target can change what is
     drawn.
   - Run the check from step 2 before and after the change.
6. **Report:**
   - The net change in lines, as measured.
   - What you removed, merged, made flat and renamed, one line each.
   - If the architecture changed, the Mermaid diagram of the architecture after the change.
   - Each change in behavior that the user approved.
   - What you saw but did not change, and why: too big, out of scope, or a decision for the user.

## Area review

1. **Select one area** with the user, as the project defines an area. A larger request is more than one review.
2. **Read what you judge the area against:** Simplest first, the code standards, the root README and the doc in
   `docs/` for the area. Then read each file in the area, and how the rest of the code calls it.
3. **Find the changes** above.
4. **Write the review** to `<reviews folder>/<Area>-<yyyy-MM-dd>.md`. Git does not track that folder. The review has
   these parts:
   1. **How to use it:** do one slice at a time. Read the index and the findings of that slice, not the whole file.
      Check each finding against the code first. Keep the behavior the same.
   2. **Decisions for the owner:** each finding that changes a public API, a file format, a dependency or the
      architecture, or deletes something that a user can see. Mark each one as "needs approval".
   3. **The slice plan:** the findings in slices within the slice size limit, grouped by the files that they touch.
      Give each slice its branch, its net lines and its validation level.
   4. **The index** of the findings.
   5. **Each finding** as `### <AREA>-<n>: <title>`, with its `file:line` and what is wrong, the simpler version in a
      few lines, the lines that it removes and its risk, and, when it moves types, layers or calls, a Mermaid diagram
      of the architecture before and after.
5. **Give it to the user:** the total lines that it removes, and the decisions that wait for the owner. Offer to run
   the slices that need no decision, each as a target. Run them at the same time where their files do not overlap
   ([parallelize skill](../parallelize/SKILL.md)).
