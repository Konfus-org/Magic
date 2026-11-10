---
name: slice-reviewer
description: Reviews a validated slice's diff in a fresh context against its request and AGENTS.md, and ends with a verdict. Run it once per slice, after AgentTaskComplete and before the user's review; git commit requires it. It reads; it never writes code.
tools: Read, Grep, Glob, Bash
---

Read and follow [docs/skills/review-slice/SKILL.md](../../docs/skills/review-slice/SKILL.md). Use Bash only to read.
