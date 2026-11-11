---
name: cleanup
description: Makes code smaller, simpler and clearer without changing what it does, from a plan that the user approves. Use it on your own, without being asked, whenever the user asks to clean up, refactor, simplify, tidy, untangle or improve code, or says that code feels messy, big or hard to follow.
---

# Cleanup

A cleanup makes code smaller, simpler and clearer. It does not change what the code does. The best cleanup removes
more code than it adds.

**What good code looks like here:**

- Less code. Remove before you add.
- The most elegant solution: a better data shape, or a reframing that makes the special cases disappear. A pile of
  cases is a sign to stop and think again.
- As simple as possible.
- No repeated code. Merge things that are alike, and use what already exists.
- Few layers and few abstractions. The code reads from top to bottom, and go-to-definition and a stack trace lead
  straight to it. Code that is easy to read is easy to debug.
- Few methods. A method is for reuse. Keep one flow in one long method, and split it into sections with short
  comments. Do not split it into methods that have one caller each.
- Names that tell what a thing is and does. A long name is fine when it is clear. Use the words that the code base
  already uses (Naming, in [PROJECT.md](../PROJECT.md)).
- A simple organization: each thing is where a reader looks for it first.
- The best practices of the language and its framework, as the code standards apply them. Use a current language or
  library feature where it makes the code simpler.

## Steps

1. **Find the target.** Name the files and members that the user means. If the target is bigger than one slice, use
   the [simplify-review skill](../simplify-review/SKILL.md) first. Then do each of its slices with this skill.
2. **Read before you change.** Read the target, its callers and the README of its area. Write down what the target
   does now, and the check that shows it.
3. **Find the changes,** in this order:
   1. Remove what nothing needs: code that nothing calls, members, parameters and overloads without a caller, flags
      and mode fields, state that you can compute, and guards for cases that cannot happen.
   2. Merge what repeats. Use an existing method, type or library feature in place of new code.
   3. Remove layers: types that only forward, interfaces with one implementation, and helpers with one caller.
   4. Put a method with one caller back into its caller, as a section with a short comment.
   5. Find a better data shape or a reframing, so that the special cases disappear.
   6. Flatten deep nesting with guard clauses and early returns.
   7. Use the API shape that fits each call site (API shape, in [PROJECT.md](../PROJECT.md)).
   8. Make the names and the folders say what each thing is and does.
   9. Fix what breaks a best practice of the language or its framework.
4. **Do not add** a new abstraction, base type, option, setting or generic system, or anything for a later need. If
   the simple version does not fit, stop and tell the user why.
5. **Write the cleanup plan.** Get the user's yes before you edit. Use the proposal of the
   [slice skill](../slice/SKILL.md) (step 6). In a swarm, end with `STATE: blocked` and put the plan in the message.
   The plan holds:
   - Each change in one line, with its reason.
   - A code example before and after each important change, short enough to read at a glance.
   - When the change moves types, layers or calls, a graph of the architecture before and after, as a Mermaid diagram
     in a `mermaid` code block. Do not draw a graph with ASCII characters.
   - The number of lines that it will remove, as an estimate.
   - The decisions that need the user's yes: a change to public API, a file format or a plugin interface, or the
     removal of a feature that a user can see.
   - If the target is `spike` or `prototype` code that nothing depends on yet, a note that says so
     (Modes, in [PROJECT.md](../PROJECT.md)).
6. **Do the plan, and keep the behaviour the same.** Validate at the lowest level that covers the target, or at the
   visual level when the target can change what is drawn ([PROJECT.md](../PROJECT.md)). In a swarm, build only, and skip the check from step 2 (the parallelize skill). Run the check from step 2 before and after the change.
7. **Report:**
   - The net change in lines, as measured.
   - What you removed, merged, flattened and renamed, one line each.
   - The Mermaid graph of the architecture after the change, if the architecture changed.
   - Each change in behaviour that the user approved.
   - What you saw but did not change, and why: too big, out of scope, or a decision for the user.
