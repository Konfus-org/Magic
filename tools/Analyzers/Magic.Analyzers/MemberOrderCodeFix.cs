using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Magic.Analyzers;

/// <summary>
/// The fix for <see cref="MemberOrderAnalyzer"/>: puts every member of the type in order, each keeping its comments
/// and attributes, with one blank line between members.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MemberOrderCodeFix))]
[Shared]
public sealed class MemberOrderCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [MemberOrderAnalyzer.Id];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(context.Span).FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } type)
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                "Put the members in order",
                _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(type, Reorder(type)))),
                equivalenceKey: MemberOrderAnalyzer.Id
            ),
            context.Diagnostics
        );
    }

    private static TypeDeclarationSyntax Reorder(TypeDeclarationSyntax type)
    {
        bool inInterface = type is InterfaceDeclarationSyntax;
        SyntaxTrivia newLine = type.DescendantTrivia()
            .FirstOrDefault(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));
        if (newLine == default)
            newLine = SyntaxFactory.LineFeed;

        MemberDeclarationSyntax[] ordered =
        [
            .. type
                .Members.Select((member, index) => (Member: member, Index: index))
                .OrderBy(entry => MemberOrderAnalyzer.RankOf(entry.Member, inInterface))
                .ThenBy(entry => entry.Index)
                .Select((entry, position) => WithSpacing(entry.Member, position == 0, newLine)),
        ];

        return type.WithMembers(SyntaxFactory.List(ordered));
    }

    /// <summary>
    /// <paramref name="member"/> with the blank lines before it replaced by one, or by none for the first member. A
    /// blank line between members is an end-of-line trivia at the start of the next member's leading trivia.
    /// </summary>
    private static MemberDeclarationSyntax WithSpacing(MemberDeclarationSyntax member, bool first, SyntaxTrivia newLine)
    {
        SyntaxTriviaList leading = member.GetLeadingTrivia();
        int kept = 0;
        while (kept < leading.Count && leading[kept].IsKind(SyntaxKind.EndOfLineTrivia))
            kept++;
        SyntaxTriviaList trimmed = SyntaxFactory.TriviaList(leading.Skip(kept));

        return member.WithLeadingTrivia(first ? trimmed : trimmed.Insert(0, newLine));
    }
}
