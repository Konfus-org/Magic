using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Magic.Tools.Analyzers.CodeFixes;

/// <summary>
/// Rewrites the chain MG0002 reported as <see cref="IfChain"/> shapes it, read again from the document so the fix is
/// what the rule measured.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp)]
public sealed class IfChainFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [IfChainAnalyzer.Id];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Document document = context.Document;
        SyntaxNode? root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        if (!LayoutOptions.TryRead(
            document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(root.SyntaxTree),
            out int maxLineLength,
            out int indentSize))
        {
            return;
        }

        SourceText text = await document.GetTextAsync(context.CancellationToken).ConfigureAwait(false);
        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            if (root.FindToken(diagnostic.Location.SourceSpan.Start).Parent is not IfStatementSyntax ifStatement)
                continue;

            if (IfChain.Find(ifStatement, text, maxLineLength, indentSize) is not { IsInShape: false } chain) continue;

            CodeAction action = CodeAction.Create(
                "Lay out the if and its branches",
                _ => Task.FromResult(document.WithText(text.WithChanges(new TextChange(chain.Span, chain.Shaped)))),
                IfChainAnalyzer.Id);
            context.RegisterCodeFix(action, diagnostic);
        }
    }
}
