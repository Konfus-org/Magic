---
name: cleanup
description: Makes code smaller, simpler and clearer without a change to what it does, from a plan that the user approves. Use it on your own, without being asked, each time the user asks to clean up, refactor, simplify, tidy, untangle or improve code, or says that code feels messy, big or hard to follow.
---

# Cleanup

A cleanup makes code smaller, simpler and clearer. It does not change what the code does. The best cleanup removes
more code than it adds. The rules are in [PROJECT.md](../PROJECT.md).

**Good code here has these qualities:**

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

## Steps

1. **Find the target.** Name the files and members that the user means. If the target is too big for one slice, use
   the [simplify-review skill](../simplify-review/SKILL.md) first. Then do each of its slices with this skill.
2. **Read before you change.** Read the target, its callers and the doc in `docs/` for its area, such as
   `docs/RENDERING.md` for a rendering gem or `docs/SHADERS.md` for the shader library. Write down what the target
   does now, and the check that shows it.
3. **Find the changes,** in this order:
   1. Remove what nothing needs:
      - Code that nothing calls, and members, parameters and overloads without a caller.
      - Flags and mode fields, and state that you can compute.
      - Guards for cases that cannot occur.
   2. Merge what repeats. Use a method, type or library feature that exists in place of new code.
   3. Remove layers: types that only forward calls, interfaces with one implementation, and helpers with one caller.
   4. Put a method with one caller back into its caller, as a section with a short comment. When you put a method
      into a constructor, build the result. An analyzer can then report an error in the code that calls the
      constructor.
   5. Find a better data shape or a different frame that removes the special cases.
   6. Make deep nesting flat with guard clauses and early returns.
   7. Use the API shape that fits each call site (API shape).
   8. Make the names and the folders say what each thing is and does.
   9. Fix what breaks a best practice of the language or its framework.
4. **Do not add** a new abstraction, base type, option, setting or general system, or anything for a later need. If
   the simple version does not fit, stop and tell the user why.
5. **Write the cleanup plan.** Get the user's yes before you edit. Use the proposal of the
   [slice skill](../slice/SKILL.md) (step 6). In a swarm, end with `STATE: blocked`, with the plan in the message.
   The plan has:
   - Each change in one line, with its reason.
   - Code before and after each important change, short enough to read quickly.
   - When the change moves types, layers or calls: a Mermaid diagram of the architecture before and after. Draw it
     as the [ste-docs skill](../ste-docs/SKILL.md), section 7, says.
   - An estimate of the lines that it removes.
   - The decisions that need the user's yes. These are a change to a public API, a file format or a plugin
     interface, and the removal of a feature that a user can see.
   - If the target is `spike` or `prototype` code that nothing depends on yet, a note that says so (Modes).
6. **Do the plan, and keep the behavior the same.**
   - Validate at the lowest level that covers the target. Use the visual level when the target can change what is
     drawn.
   - Run the check from step 2 before and after the change. In a swarm, build only and do not run the check
     (the parallelize skill).
7. **Report:**
   - The net change in lines, as measured.
   - What you removed, merged, made flat and renamed, one line each.
   - If the architecture changed, the Mermaid diagram of the architecture after the change. Draw it as the
     [ste-docs skill](../ste-docs/SKILL.md), section 7, says.
   - Each change in behavior that the user approved.
   - What you saw but did not change, and why: too big, out of scope, or a decision for the user.
