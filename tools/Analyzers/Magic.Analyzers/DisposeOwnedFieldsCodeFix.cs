using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Magic.Analyzers;

/// <summary>
/// The fix for <see cref="DisposeOwnedFieldsAnalyzer"/> in a class that derives from <c>Disposable</c>: wraps the
/// <c>new</c> of the field in <c>Track(...)</c>. A field initializer cannot call <c>Track</c>, and another class
/// disposes in its own way, so there the author makes the change.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DisposeOwnedFieldsCodeFix))]
[Shared]
public sealed class DisposeOwnedFieldsCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [DisposeOwnedFieldsAnalyzer.Id];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            if (diagnostic.AdditionalLocations.Count != 1)
                continue;

            Location location = diagnostic.AdditionalLocations[0];
            if (location.SourceTree != root.SyntaxTree)
                continue;

            if (root.FindNode(location.SourceSpan, getInnermostNodeForTie: true) is not ExpressionSyntax creation)
                continue;

            InvocationExpressionSyntax tracked = SyntaxFactory
                .InvocationExpression(
                    SyntaxFactory.IdentifierName("Track"),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(creation.WithoutTrivia()))
                    )
                )
                .WithTriviaFrom(creation);
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Track the field",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(creation, tracked))),
                    equivalenceKey: DisposeOwnedFieldsAnalyzer.Id
                ),
                diagnostic
            );
        }
    }
}
