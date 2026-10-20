# Contributing

## Code of conduct

Read the [Code of Conduct](CODE_OF_CONDUCT.md). It applies in every space of the project.

## Standards

Code follows the [Code Standards](CODESTANDARDS.md), tests follow [Tests](tests/README.md), and documentation
follows the documentation rules in [AGENTS.md](AGENTS.md#documentation-rules), which apply to every contributor, not
only agents. Before changing an area, read its README: it says how the area works and what must hold for it to work,
and a change to how it works updates it in the same change.

## Building

The [README](README.md) has the requirements, the build and the command line. `tools/Validate.ps1` runs what every
change must pass ([Tools](tools/README.md)).

## Reviewing and merging

Make a fork with your changes, then open a pull request into `dev`. A maintainer reviews, comments, and approves once
every comment is addressed. Every check must pass before a merge: the build on every OS, the tests, the
documentation's links and the security analysis ([CI](.github/workflows/README.md)). Merging to `main` publishes a
release.

A reviewer looks for the same things in every change, so a contributor can check them first: it follows the
[Code Standards](CODESTANDARDS.md); it is the smallest change that does the job; a fix reaches every copy of the
code it fixes, not only the one that was reported; nothing is added without a caller; nothing the build generates is
edited by hand; no secret is committed; and what the change touched is left cleaner than it was found, with the
cleanup told apart from the fix so each can be judged on its own.

Branches are short-lived and merge by fast-forward once validated. CI runs the same `tools/Validate.ps1` a
developer runs, so a change that passes locally passes there, and `main` is always releasable, since merging to it
publishes.

## AI-assisted work

[AGENTS.md](AGENTS.md) is the process AI agents follow in this repository. AI-assisted contributions are welcome
under the same standards as any other code: you must understand, be able to explain, and be ready to change what you
submit, and a human reviews and approves everything before it merges.
