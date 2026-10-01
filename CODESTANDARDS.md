# Toybox CodeStandard

## Core Engineering Policies

- **Scope**: Keep changes isolated and highly reusable.
- **Duplication**: Avoid redundant code patterns without building single-use helper functions.
- **Simplicity**: Prioritize the simplest, most direct solution first. Avoid over-engineering, unnecessary abstractions, or predicting future edge cases. Add complexity only when a specific problem requires it.
- **Housekeeping**: Permanently delete stale definitions instead of leaving commented placeholders.

## Code style

These rules apply to all C# in the repository. New code must follow them.

### Type layout

Members go in this order, with one blank line between members and between groups:

1. Constants and static fields
2. Instance fields
3. Constructors and finalizer
4. Events, then properties
5. Public and internal methods
6. Private methods
7. Interface implementations (`Dispose` and similar)
8. Nested types

Within a group, public comes before internal, and internal before private. Keep related members next to
each other.

### State

- Public and internal state is exposed as `PascalCase` properties, never as fields.
- If a value is set once, use `{ get; }` and assign it in the constructor.
- If a value can change, use `{ get; set; }`, or `{ get; private set; }` when writes need a tighter scope.
- If a value is derived from other state, use an expression-bodied property (`=>`).
- Private fields are only for injected dependencies, locks, internal collections, and backing fields that do
  real work such as validation.
- The exception is interop and data structs, where layout or the JSON shape depends on fields
  (`[StructLayout]`, GPU structs, ECS components). They keep their public fields.

### Methods

A method should read from top to bottom:

1. Guard clauses and validation first, returning or throwing early.
2. Setup next, using `using` declarations rather than nested `using` blocks.
3. The main path, kept at one level of indentation where possible. Prefer early `continue` and `return` to
   nested `if`/`else`.
4. The result.
5. Local functions last, after the final `return`.

Separate these steps with a blank line. Long methods are fine as long as they read in order.

### Comments

- XML doc comments on types and non-obvious members. Say why, not what.
- No divider comments (`// ---- Section ----`, `// ====`) and no `#region`.


