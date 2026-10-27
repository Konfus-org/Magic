# Magic.Analyzers

The rules of [Code Standards](../../../docs/CODE_STANDARDS.md) that no built-in rule checks, as a Roslyn analyzer
every other project in the solution runs through `Directory.Build.props`. A rule's id is `MG` and a number; each is one
class whose summary says what it checks and names the standard, and the build and the editor report it like any other
rule. `.editorconfig` sets the severities.

Every rule has a code fix, in a class beside it named for the rule with `CodeFix` in place of `Analyzer`, so the
editor offers it and `tools/Scripts/Format.ps1` can apply it. A rule whose fix would need a decision the author must
make still has one: it does the mechanical part and leaves the decision visible.

To add a rule: the analyzer and its fix, a line in `AnalyzerReleases.Unshipped.md`, the severity in `.editorconfig`,
and the standard it enforces named in its summary. Visual Studio keeps the analyzer it loaded; restart it after a
change here.
