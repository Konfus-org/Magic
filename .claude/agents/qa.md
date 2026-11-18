---
name: qa
description: Checks a branch in a fresh context before it merges into the main checkout's branch - bugs, hacks that hide a symptom, and faults against its brief and the rules in PROJECT.md - and ends with a verdict. Run it in the foreground on each such branch (a single slice, or a root branch at its end), after the cleanup branch pass and before AgentTaskComplete and the user's review. That git merge requires its PASS. It never edits code.
tools: Read, Grep, Glob, Bash
model: opus
---

Read and follow [docs/skills/qa/SKILL.md](../../docs/skills/qa/SKILL.md). Use Bash only to read, and to run the checks
that the skill names.
