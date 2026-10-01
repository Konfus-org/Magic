# Tests

```
Tests/
  Magic.UnitTests/          Unit tests: in-memory, milliseconds, run before every commit
  Magic.IntegrationTests/   Integration tests: real files, the asset watcher, native loaders, gem DLLs
  TestGem/                  The smallest gem there is, for the gem loader tests
```

```powershell
dotnet test Tests\Magic.UnitTests           # the unit suite
dotnet test Tests\Magic.IntegrationTests    # the integration suite
```

Both build to `Build\...\bin\Tests\`, beside `bin\Tests\Gems\` where TestGem lands.

## Every test

- **One class, one file.** `FooTests.cs` tests `Foo` and nothing else, in the folder that mirrors where `Foo` lives.
  A fake or helper shared by several test classes gets a file of its own (`Fakes\`).
- **One thing.** A test checks one behavior of one method or property. If the name needs an "and", or the asserts
  need a comment to tell them apart, split it.
- **Arrange, act, assert**, in that order, once each, with a blank line between. Nothing is asserted before the
  act, and there is no second act after the assert: that is a second test.
- **No shared state.** Whatever a test needs it builds in its own arrange. Nothing static and mutable, nothing that
  one test leaves for the next, no dependence on the order tests run in.
- **Our code only.** Third-party and framework code is trusted to work: no test of what System.Text.Json reads,
  what CommandLineParser parses, what flecs matches or what System.Numerics computes. Test what we wrote on top.
- **No defaults.** A test that only checks what a value starts as breaks every time the default is tuned and
  catches nothing. Test behavior that changes state, and the gates that turn bad input away.
- **Cheap to maintain.** Assert the behavior, not the wording: no exact log lines, window layouts or other text
  that changes whenever the feature is polished. No restating the implementation as the expected value.
- Name a test after the behavior it checks, as a sentence: `A_disposed_watch_hears_nothing_more`.
- Use `[Theory]` with `[InlineData]` for the same behavior over several inputs, not a loop inside one test.

## Unit tests

- **One unit.** A unit test drives one class through its public or internal interface. No end to end: a test that
  needs two subsystems working together is an integration test.
- **No I/O.** Never the file system, the network, a native library, the clock or an unseeded `Random`: anything
  that can fail for a reason other than the code being wrong.
- **No fixture.** A unit test class has no fields but constants; each test news up what it tests.
- **Don't over-mock.** Fake the gem boundaries (`Fakes\FakeRendering`). Use the real types for plain data and math.

Code that only works through process-wide state (`Debugging.UI`, `Debugging.Commands`) has no unit tests. Pull the
logic out into something that can be called on its own (`ConsoleSystem.Split`) and test that.

## Integration tests

The integration suite is for our code where it meets something real: files on disk and the file watcher, the SDL
and Assimp loaders, gem DLLs, the Flecs adapter, and Core systems running on it. The rules under "Every test"
still hold; what is different:

- A test class may keep the thing under test and its temp folder in fields, built in the constructor and disposed
  in `Dispose`. xUnit makes a new instance per test, so nothing is shared.
- Use `TempFolder` for files: a fresh GUID folder, deleted on dispose. Write the files before opening the service
  that reads them, so only the tests about the watcher wait on it.
- A wait on a worker or the watcher is a bounded loop (`StepUntil`), never a bare sleep followed by a hope.
- Tests that initialise process-wide SDL go in the `SdlCollection`, so they run one at a time.
