# Contributing

- Follow the [Code of Conduct](CODE_OF_CONDUCT.md) in every space of the project.
- Code follows the [Code Standards](CODE_STANDARDS.md), tests follow [Tests](../tests/README.md), and docs follow the
  [documentation rules](../AGENTS.md#documentation-rules).
- Before changing an area, read its README. If you change how the area works, update its README in the same change.
- The [README](../README.md) covers the requirements and the build. Format your change with
  `tools/Scripts/Format.ps1` and pass `tools/Scripts/Validate.ps1` ([Tools](../tools/README.md)).
- Once per clone, run `git config core.hooksPath .githooks`. The hooks start each commit's subject with
  `type: ` and refuse one without it ([Git hooks](../.githooks/README.md)).

## Pull requests

- Open a pull request from your fork into `dev`. Keep branches short-lived; they merge by fast-forward.
- A maintainer reviews it and approves once every comment is addressed.
- Every check must pass before a merge: the build on Windows, Linux and macOS, the tests, the commit subjects, the
  docs' links and the security analysis ([CI](../.github/workflows/README.md)). Merging to `main` publishes a release.
- A reviewer checks that the change follows the Code Standards, is the smallest change that does the job, fixes
  every copy of the code it fixes, adds no private or internal code without a caller, edits nothing the build
  generates, commits no secret, and leaves what it touched cleaner, with the cleanup kept apart from the fix.

## AI-assisted work

AI-assisted contributions follow the same standards. You must understand, be able to explain and be ready to change
what you submit. A human reviews and approves everything before it merges. Agents follow [AGENTS.md](../AGENTS.md).
