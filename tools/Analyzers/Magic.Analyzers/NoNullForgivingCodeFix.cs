using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Magic.Analyzers;

/// <summary>
/// The fix for <see cref="NoNullForgivingAnalyzer"/>: takes the <c>!</c> off, so the compiler reports the null it was
/// hiding where it belongs, for the author to check or carry in a <c>Result</c>.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NoNullForgivingCodeFix))]
[Shared]
public sealed class NoNullForgivingCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [NoNullForgivingAnalyzer.Id];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(context.Span) is not PostfixUnaryExpressionSyntax suppression)
            return;

        ExpressionSyntax operand = suppression.Operand.WithTriviaFrom(suppression);
        context.RegisterCodeFix(
            CodeAction.Create(
                "Remove '!'",
                _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(suppression, operand))),
                equivalenceKey: NoNullForgivingAnalyzer.Id
            ),
            context.Diagnostics
        );
    }
}
