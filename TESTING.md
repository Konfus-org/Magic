# Tests

Unit tests only, for now. Each project that has any keeps them in a `Tests` folder beside its source, as a project
of its own named `<Project>.Tests`. Today only Core has any, one project with a folder per area, as its source has:

```
Magic/Core/Tests/Core.Tests.csproj
Magic/Core/Tests/<Area>/<Type>Tests.cs      Assets, Debug, Maths, Randomness, Rendering, World
```

```powershell
dotnet test Magic\Core\Tests                                         # Core's tests
dotnet test Magic\Core\Tests --filter FullyQualifiedName~AabbTests   # one class's
dotnet test Magic-Wizard.slnx                                        # all of them
```

`Directory.Build.props` makes any `*.Tests` project a test project (xunit, a reference to Magic, output in
`Build\...\bin\Tests\`) and leaves every project's `Tests` folder out of its own build: a gem never carries its tests.
A test project references what it tests, and only plain project references besides.

## Every test

- **One class, one file.** `FooTests.cs` tests `Foo` and nothing else, in the `Tests` folder of the project `Foo`
  is in (in Core, under the area `Foo` is in: `Tests/Maths/AabbTests.cs` for `Contexts/Maths/Aabb.cs`). Nothing is
  shared between test files: a fake a test needs is a small private class in its file.
- **One thing.** A test checks one behavior of one method or property. If the name needs an "and", or the asserts
  need a comment to tell them apart, split it.
- **Arrange, act, assert**, in that order, once each, with a blank line between. Nothing is asserted before the
  act, and there is no second act after the assert: that is a second test.
- **No shared state.** Whatever a test needs it builds in its own arrange. Nothing static and mutable, nothing that
  one test leaves for the next, no dependence on the order tests run in.
- **The public API only.** A test drives a type through its public members and sees what happened where a gem
  would: a return value, an event, what a fake it was handed received. Nothing internal is visible to a test (no
  `InternalsVisibleTo`), and there is no reflection or private state: what is not public is not tested.
- **Nothing is exposed for a test.** No member is added or made public so that a test can reach it. What other
  projects use is public; what they do not is not, and is tested only through the public member that uses it.
- **Our code only.** Third-party and framework code is trusted to work: no test of what System.Text.Json reads,
  what CommandLineParser parses, what flecs matches or what System.Numerics computes. Test what we wrote on top.
- **No defaults.** A test that only checks what a value starts as breaks every time the default is tuned and
  catches nothing. Test behavior that changes state, and the gates that turn bad input away.
- **Cheap to maintain.** Assert the behavior, not the wording: no exact log lines, window layouts or other text
  that changes whenever the feature is polished. No restating the implementation as the expected value.
- Name a test after the behavior it checks, as a sentence: `A_disposed_watch_hears_nothing_more`.
- Use `[Theory]` with `[InlineData]` for the same behavior over several inputs, not a loop inside one test.

## Unit tests

- **One unit.** A unit test drives one class through its public API. No end to end: two subsystems working
  together are not a unit test, and are not tested for now.
- **No I/O.** Never the file system, the network, a native library, the clock or an unseeded `Random`: anything
  that can fail for a reason other than the code being wrong.
- **No fixture.** A unit test class has no fields but constants; each test news up what it tests.
- **Don't over-mock.** Fake the interfaces at the edge of what is tested (an `IServices`, an `ITelemetry`). Use the
  real types for plain data and math.
