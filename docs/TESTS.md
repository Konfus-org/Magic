# Tests

The unit tests check one class at a time, in Core and in the gems.

## Run the tests

- To run all the tests, run `dotnet test --solution src/Magic.slnx`.
- To run one test project, run `dotnet test --project tests/<Name>/<Name>.csproj`.

## Test projects

Each tested project has one xunit test project in `tests/`:

| Tested project | Test project               | Root namespace      |
| -------------- | -------------------------- | ------------------- |
| `src/Core`     | `tests/Magic.Core.Tests/`  | `Magic.Tests`       |
| A gem          | `tests/<Gem>.Tests/`       | `Magic.<Gem>.Tests` |

- A test project references the project that it tests with a plain `<ProjectReference>`.
- A gem gets a test project only when it has types with behaviour.
- The tests must pass on all supported operating systems. Make paths with `IFileSystem.Combine`. Do not write Windows
  paths, for example `C:\` or `\\`.

> **Note:** An agent writes a new test only when the user asks for one ([Structure](../AGENTS.md#structure)).

## What a test covers

- **One unit:** A test checks one class. Do not write end-to-end tests.
- **No I/O:** A test does not use the file system, the network, the clock or an unseeded `Random`. A test can load a
  native library only when the code that it tests calls that library.
- **No fixture:** A test class has no fields, except constants. Each test makes the object and the data that it checks.
- **Tested project only:** Check what the tested project can see: a return value, an event, or a call that a fake
  received. A test project can use the internal members of the project that it tests, through `InternalsVisibleTo` in
  the csproj of that project. Do not use reflection or private state.
- **Nothing added for a test:** Do not add a member, or make a member more visible, only so that a test can get to it.
- **Our code only:** Do not test framework code or third-party code. Test the code that we wrote on top of it.
- **No shared state:** Do not use static mutable state. A test must not leave state for the next test. A test must not
  depend on the test order.

## How to write a test

- **One class, one file:** `FooTests.cs` tests `Foo`. Put the file in the test project and folder that match the source
  file. For example, `tests/Magic.Core.Tests/Utils/ResultTests.cs` tests `src/Core/Utils/Result.cs`.
- **Nothing shared between files:** Test files do not share code. Write a fake as a small private class in its test
  file.
- **One behaviour:** A test checks one behaviour of one method or property. If the test name needs "and", make two
  tests.
- **Arrange, act, assert:** A test has one arrange part, one act part and one assert part, in that order. Put a blank
  line between the parts.
- **Fails first:** Before you write a fix, write a test for it. Make sure that the test fails without the fix.
- **A sentence as the name:** The name of a test is a sentence that says the behaviour, for example
  `A_failure_says_why`.
- **`[Theory]` for many inputs:** To check one behaviour with different inputs, use `[Theory]` with `[InlineData]`. Do
  not use a loop in one test.
- **No start values:** Do not test the value that a property has at the start. Test the behaviour that changes the
  state, and the checks that reject bad input.
- **Behaviour, not text:** Do not check exact log lines, window layouts or other text. Do not copy the implementation
  to get the expected value.
- **Fakes only at the edge:** Make fakes only for the interfaces at the edge, for example `IFileSystem`. Use the real
  types for plain data and math.
