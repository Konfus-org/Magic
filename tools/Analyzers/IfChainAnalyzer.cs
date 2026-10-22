using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Magic.Tools.Analyzers;

/// <summary>
/// MG0002: an <c>if</c> and its <c>else</c>s whose bodies are each one statement take <see cref="IfChain"/>'s shape:
/// no braces, each body beside its header when the line fits and below it when not, so how a choice is laid out
/// follows from its length and never from who wrote it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IfChainAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "MG0002";

    private static readonly DiagnosticDescriptor _rule = new(
        Id,
        "An if or else whose body is one statement has no braces, and its body is beside it when the line fits",
        "Lay out this if and its else branches without braces, each body beside its header if it fits, else below it",
        "Layout",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every branch's body goes on its header's line when that line fits within max_line_length, and "
            + "on the next line, one indent in, when it does not; braces only where a header or a body fits on no "
            + "line.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.IfStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        IfStatementSyntax ifStatement = (IfStatementSyntax)context.Node;
        if (!LayoutOptions.TryRead(
            context.Options.AnalyzerConfigOptionsProvider.GetOptions(ifStatement.SyntaxTree),
            out int maxLineLength,
            out int indentSize))
        {
            return;
        }

        IfChain? chain = IfChain.Find(
            ifStatement,
            ifStatement.SyntaxTree.GetText(context.CancellationToken),
            maxLineLength,
            indentSize);
        if (chain is not { IsInShape: false }) return;

        context.ReportDiagnostic(Diagnostic.Create(_rule, ifStatement.IfKeyword.GetLocation()));
    }
}
