using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Magic.Tools.Analyzers;

/// <summary>
/// MG0001: a statement that leaves its block (<c>return</c>, <c>continue</c>, <c>break</c>, <c>throw</c>) has a blank
/// line before it, so where a block ends early stands out from the work before it. An early-out <c>if</c>, whose body
/// is one of them alone, takes the blank line in its body's place, and a run of them shares one, as a group of guard
/// clauses reads as one step. What opens its block needs none, and nor does the <c>break</c> that ends a
/// switch section, which is the section's syntax rather than a way out.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlankLineBeforeExitAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "MG0001";

    private static readonly DiagnosticDescriptor _rule = new(
        Id,
        "A way out of a block has a blank line before it",
        "Put a blank line before this {0}",
        "Layout",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A return, continue, break or throw, or an if whose body is one of them, has a blank line before "
            + "it unless it opens its block or follows another such if.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Analyze,
            SyntaxKind.ReturnStatement,
            SyntaxKind.ContinueStatement,
            SyntaxKind.BreakStatement,
            SyntaxKind.ThrowStatement,
            SyntaxKind.IfStatement);
    }

    /// <summary>
    /// Whether <paramref name="statement"/> is an <c>if</c> whose body is a way out and nothing else, as a guard clause
    /// is. Braces or none: MG0002 takes them off, and this holds the same before it has as after.
    /// </summary>
    private static bool IsEarlyOut(StatementSyntax statement)
    {
        if (statement is not IfStatementSyntax ifStatement) return false;

        StatementSyntax body = ifStatement.Statement;
        if (body is BlockSyntax { Statements.Count: 1 } block) body = block.Statements[0];

        return IsWayOut(body);
    }

    private static bool IsWayOut(StatementSyntax statement)
        => statement is ReturnStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax
            or ThrowStatementSyntax;

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        StatementSyntax statement = (StatementSyntax)context.Node;
        if (statement is IfStatementSyntax && !IsEarlyOut(statement)) return;

        // A way out that is a body of its own, an if's or a loop's, is not a step of a block; its if is.
        SyntaxList<StatementSyntax> siblings;
        if (statement.Parent is BlockSyntax block) siblings = block.Statements;
        else if (statement.Parent is SwitchSectionSyntax section) siblings = section.Statements;
        else return;

        int index = siblings.IndexOf(statement);
        if (index <= 0) return;

        if (statement is BreakStatementSyntax && statement.Parent is SwitchSectionSyntax && index == siblings.Count - 1)
            return;

        if (statement is IfStatementSyntax && IsEarlyOut(siblings[index - 1])) return;

        if (HasBlankLineBefore(statement)) return;

        SyntaxToken keyword = statement.GetFirstToken();
        context.ReportDiagnostic(Diagnostic.Create(_rule, keyword.GetLocation(), keyword.ValueText));
    }

    /// <summary>
    /// Whether the first line of what comes before <paramref name="statement"/> on its own lines, its comments
    /// included, is empty: the blank line goes above a comment that belongs to the statement.
    /// </summary>
    private static bool HasBlankLineBefore(StatementSyntax statement)
    {
        foreach (SyntaxTrivia trivia in statement.GetFirstToken().LeadingTrivia)
        {
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia)) continue;

            return trivia.IsKind(SyntaxKind.EndOfLineTrivia);
        }

        return false;
    }
}
