---
name: parallelize
description: Runs a large effort as a swarm. Coders build slices at the same time, and the coordinator merges each one into one root branch. Then a cleanup pass, QA and one finish of the root follow, with one merge into the main checkout's branch after the user's yes. Use it when the user asks for a swarm or for work in parallel. Use it on your own only when a list has four or more slices that do not depend on each other. Smaller efforts run one slice after the other (the slice skill).
---

Read and follow [docs/skills/parallelize/SKILL.md](../../../docs/skills/parallelize/SKILL.md).
