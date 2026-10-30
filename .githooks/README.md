# Git hooks

Git runs these once `core.hooksPath` names this folder: `git config core.hooksPath .githooks`, once per clone, which
`tools/Scripts/AgentTaskComplete.ps1` also sets ([Commits](../AGENTS.md#commits)).

- `prepare-commit-msg` starts a commit's subject with `type: `, such as `feat: `. The type comes from the branch's
  name, or from the staged files when they are all Markdown, CI, build or test files.
- `commit-msg` refuses a commit whose subject still doesn't start with `type: `, or has a scope such as
  `feat(core): `.
