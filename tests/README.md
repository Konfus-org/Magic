# Tests

The unit tests, one project per gem or library under test, named `<Gem>.Tests`. They run on xunit through the
Microsoft testing platform, which `global.json` selects.

- **Run them all:** `dotnet test --solution src/Magic.slnx`.
- **Run one project:** `dotnet test --project tests/<Gem>.Tests/<Gem>.Tests.csproj`.
- A test project sees the gem's internal members through `InternalsVisibleTo` in the gem's project file.
- A test builds what it checks from small data in the test itself, never from an asset on disk.
