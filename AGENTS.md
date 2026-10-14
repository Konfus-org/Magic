# AI Agent Guidelines

This document outlines the strict rules, workflows, and verification steps for AI agents operating in this repository. You must read and follow this guide, along with any relevant document listed below, before modifying any files.

## Required Reading

| Document | Purpose & Scope |
| --- | --- |
| [Start Here](README.md) | High-level overview of Magic and Wizard, build/run commands, engine architecture, and directory layout. |
| [Code Standards](CODESTANDARDS.md) | C# coding standards, performance rules, and cross-platform guidelines enforced by the build system. |
| [Contributing](CONTRIBUTING.md) | Code review processes and acceptance criteria. |
| [Tests](tests/README.md) | Test suites, conventions, and requirements for requested test coverage. |

## Development Workflow & Constraints

### 1. Pre-Implementation Review
- **KISS & DRY Planning:** Before modifying any code, analyze the task step-by-step. Identify the simplest, least over-engineered, and most robust solution possible.
- **Prefer Removing Code:** Prefer a solution that removes or simplifies code over one that adds it.
- **Proposal Requirement:** Present your proposed approach to the user before writing code. You must include:
  1. Your rationale for the design.
  2. Your confidence rating on a scale from 1 to 10.
  3. The alternative solutions you considered, and why the chosen one is the simplest, most maintainable, and most straightforward.
- **Do not modify code** until the user explicitly approves this proposal.

### 2. Architecture & Design Rules
- **Scope Limitation:** Keep every change as minimal and simple as possible. Match the surrounding code style and patterns. Do not introduce speculative abstractions.
- **State Management:** Prefer stateless implementations wherever possible.
- **Private State:** Private state is a private field declared at the top of the type, never a private `{ get; set; }` property. Properties are for what other code reads; behaviour on reading or writing private state is a method. See [Code Standards](CODESTANDARDS.md#state).
- **Abstractions:** Minimize inheritance, abstractions, and interfaces. Avoid unnecessary architectural overhead.
- **Data Flow:** Prioritize data-oriented programming with straightforward, explicit data flows.
- **Dependencies:** All third-party libraries must come from NuGet. Do not vendor or manually embed external code.

### 3. Documentation & Output
- **Synchronized Docs:** Update relevant comments, docstrings, and markdown files (`.md`) immediately alongside code changes. Keep documentation completely accurate.
- **Explain, Never Inventory:** Docs say how a thing works and what must hold for it to work, never what is in which folder, file or namespace. No lists or tables of the files, assets, ids, types or packages there are, and no folder trees beyond where each kind of thing goes: the Materials README explains how materials work, not which materials there are. The folder already shows what is in it, and a list is one more thing to keep in step that quietly goes out of date. A specific file is named only as the example that teaches a rule, or where the behaviour being explained depends on that path.
- **Gems by Role, Never by Name:** Gems can be swapped at any time, so comments, summaries, docs and user-facing messages describe another gem only by what it does ("a platform gem", "the GPU provider", "a shader compiler"), never by its name or the library it wraps. A gem names itself only inside its own folder. Configuration that must list gems (project files, `Magic.slnx`, `.editorconfig` sections) is exempt.
- **Chain of Thought:** Think out loud throughout the task execution. 
- **Justification & Diff:** At the completion of your task, provide an in-depth justification of your changes accompanied by a complete `git diff` preview.

### 4. Testing & Scope Changes
- **Test Requirements:** Do not write new tests unless explicitly requested by the user. Always run existing tests if your changes modify code covered by them.
- **Sweeping Changes:** For large or multi-file architectural changes, you must submit a pre-implementation plan containing "before/after" code snippets and a data-flow diagram. This must be approved before editing.

## Git & Workspace Management

- **Branch Locking:** Work exclusively on the currently active branch of the main checkout. Do not create new branches, stashes, or worktrees.
- **No Self-Committing:** Do not execute commits unless explicitly instructed by the user.
- **AI Anonymity:** Never add a co-author line. Ensure no code, comments, or commit messages hint that an AI generated the work.
- **Multi-Agent Workspace Isolation:** Other agents may be editing this checkout simultaneously. Before staging or committing (when requested), check `git status`. Stage only your own file modifications or specific hunks; do not alter or commit other uncommitted work.

## Validation & Verification Steps

Execute these verification checks in order before concluding your work:

1. **Clean Build Compilation:** 
   Build `Magic.slnx`. Treat all relevant compiler warnings as errors. A perfectly clean build is mandatory.
2. **Runtime Execution Verification:** 
   Run the host application on the samples, headless and with a window, using the strict error-handling flag:
   ```bash
   Magic --project samples --frames 120 --fail-on-error
   Magic --project samples --frames 120 --fail-on-error --headless
   ```
   *(Note: This returns exit code `2` if any error was logged during the run.)*
3. **Visual Check (If Applicable):** 
   For any visual or rendering change, run the samples with a window, take a screenshot and look at it. The frames
   before the scene is drawn go by quickly, so wait well past them; the log names the file, under `Screenshots/`
   beside Magic:
   ```bash
   printf 'wait 1500\nwait 400\nscreenshot\n' | Magic --project samples --frames 2000 --fail-on-error
   ```
