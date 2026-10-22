using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;

namespace Magic.Tools.Analyzers.CodeFixes;

/// <summary>
/// Puts the blank line MG0001 asks for above the statement, and above any comment that belongs to it.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp)]
public sealed class BlankLineBeforeExitFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [BlankLineBeforeExitAnalyzer.Id];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Document document = context.Document;
        SyntaxNode? root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            SyntaxToken keyword = root.FindToken(diagnostic.Location.SourceSpan.Start);
            SyntaxToken spaced = keyword.WithLeadingTrivia(keyword.LeadingTrivia.Insert(0, SyntaxFactory.LineFeed));
            CodeAction action = CodeAction.Create(
                "Put a blank line before it",
                _ => Task.FromResult(document.WithSyntaxRoot(root.ReplaceToken(keyword, spaced))),
                BlankLineBeforeExitAnalyzer.Id);
            context.RegisterCodeFix(action, diagnostic);
        }
    }
}
