# Tests

Run them with `dotnet test Magic.slnx`.

- Each tested project has one xunit project here named `<Project>.Tests`: `tests/Core.Tests/` for `src/Core`,
  `tests/<Gem>.Tests/` for a gem. Its root namespace is `Magic.Tests` for Core and `Magic.<Gem>.Tests` for a gem.
- A test project references what it tests, and nothing but plain project references besides.
- A gem gets a test project only once it has public types with behaviour.
- Tests pass on every supported OS. Build paths with `IFileSystem.Combine`, never Windows-shaped (`C:\`, `\\`).

## What a test covers

- **One unit:** one class, driven through its public API. No end-to-end tests.
- **No I/O:** no file system, network, native library, clock or unseeded `Random`.
- **No fixture:** a test class has no fields but constants. Each test creates what it tests.
- **Public API only:** check what a gem would see: a return value, an event, what a fake received. No
  `InternalsVisibleTo`, reflection or private state.
- **Nothing exposed for a test:** never add a member or make one public so a test can reach it.
- **Our code only:** don't test framework or third-party code. Test what we wrote on top.
- **No shared state:** nothing static and mutable, nothing left for the next test, no dependence on test order.

## How a test is written

- **One class, one file:** `FooTests.cs` tests `Foo`, in the matching test project and folder:
  `tests/Core.Tests/Contexts/Utils/ResultTests.cs` for `src/Core/Contexts/Utils/Result.cs`. Nothing is shared between
  test files; a fake is a small private class in its file.
- **One behaviour** of one method or property. If the name needs an "and", split it.
- **Arrange, act, assert,** once each, in that order, with a blank line between.
- **Fails first:** write a test for a fix before the fix, and see it fail without it.
- **Named as a sentence** for the behaviour it checks: `A_failure_says_why`.
- **`[Theory]` with `[InlineData]`** for one behaviour over several inputs, not a loop in one test.
- **No defaults:** don't test what a value starts as. Test behaviour that changes state, and the checks that reject
  bad input.
- **Assert behaviour, not wording:** no exact log lines, window layouts or other text. Don't restate the
  implementation as the expected value.
- **Don't over-mock:** fake the interfaces at the edge (an `IFileSystem`, an `ICommands`). Use real types for plain
  data and math.
