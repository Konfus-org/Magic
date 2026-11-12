# Contributing

Follow the [Code of Conduct](CODE_OF_CONDUCT.md) in every space of the project.

## Make a change

1. Read the [README](../README.md) for the requirements and the build.
2. Once for each clone, run `git config core.hooksPath .githooks` ([Git hooks](TOOLS.md#git-hooks)).
3. Make a branch from `dev`.
4. Before you change an area, read its doc in `docs/`, such as [Rendering](RENDERING.md) or [Shaders](SHADERS.md).
5. Make the change.
6. If the change alters how an area works, update its doc in the same change.
7. Format the change with `tools/Scripts/Format.ps1`.
8. Run `tools/Scripts/Validate.ps1`. All its steps must pass ([Tools](TOOLS.md)).
9. Open a pull request from your fork into `dev`.

Code follows the [Code Standards](CODE_STANDARDS.md). Tests follow [Tests](TOOLS.md#tests). Docs follow the
[Documentation Rules](../AGENTS.md#documentation-rules).

## Pull requests

- Keep a branch short. A pull request merges by fast-forward.
- A maintainer reviews the pull request. The maintainer approves it when each comment has an answer.
- Each check must pass before a merge ([CI](CI.md)). A merge into `main` publishes a release.

The reviewer checks that the change:

- Follows the Code Standards.
- Is the smallest change that does the job.
- Fixes each copy of the code that it fixes.
- Adds no private or internal code without a caller.
- Edits no code that the build generates.
- Commits no secret.
- Leaves the code that it touches cleaner, with the cleanup kept apart from the fix.

## AI-assisted work

AI-assisted contributions follow the same standards. You must understand what you submit, explain it and be ready to
change it. A person reviews and approves each change before it merges. Agents follow [AGENTS.md](../AGENTS.md).
