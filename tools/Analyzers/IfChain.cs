using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Magic.Tools.Analyzers;

/// <summary>
/// An <c>if</c> with the <c>else if</c>s and the <c>else</c> after it, when every branch's body is one simple statement
/// on one line, and the one shape such a chain takes (MG0002): no braces, each body on its header's line where it fits
/// within the line length and on the next line, one indent in, where it does not. Where a header or a body fits on no
/// line, every branch has braces instead, as the wrap will break that line and a body or header that spans lines needs
/// them. A chain with a longer branch, or with what moving it would lose (a comment between its parts, a directive), is
/// not read: the brace rule (IDE0011) settles it. Analyzer and fix both work from this, so what is reported is what is
/// fixed.
/// </summary>
public sealed class IfChain(TextSpan span, string shaped, bool isInShape)
{
    /// <summary>
    /// The text the chain is written in, from its first <c>if</c> to the end of its last body.
    /// </summary>
    public TextSpan Span { get; } = span;

    /// <summary>
    /// The chain in its shape, to replace <see cref="Span"/>.
    /// </summary>
    public string Shaped { get; } = shaped;

    public bool IsInShape { get; } = isInShape;

    /// <summary>
    /// The chain <paramref name="ifStatement"/> starts, or null where it is not one this shapes: an <c>else if</c>
    /// (its chain starts above it), an <c>if</c> sharing its line with other code, or a chain with a branch that is
    /// not one simple statement on one line.
    /// </summary>
    public static IfChain? Find(IfStatementSyntax ifStatement, SourceText text, int maxLineLength, int indentSize)
    {
        if (ifStatement.Parent is ElseClauseSyntax) return null;

        TextLine line = text.Lines.GetLineFromPosition(ifStatement.SpanStart);
        string indent = text.ToString(TextSpan.FromBounds(line.Start, ifStatement.SpanStart));
        if (indent.Trim().Length > 0) return null;

        List<Branch> branches = [];
        foreach (Branch? branch in ReadBranches(ifStatement, text))
        {
            if (branch is null) return null;

            branches.Add(branch);
        }

        // Braces on every branch when any header or body is too long for a line of its own.
        string inner = indent + new string(' ', indentSize);
        bool isBraced = branches.Exists(branch => indent.Length + branch.Header.Length > maxLineLength
            || inner.Length + branch.Body.Length > maxLineLength);

        string newLine = text.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
        if (newLine.Length == 0) newLine = "\n";

        // Each branch's header, on its own line after the first, then its body: in braces, beside it or below it.
        StringBuilder shaped = new();
        for (int i = 0; i < branches.Count; i++)
        {
            Branch branch = branches[i];
            if (i > 0) shaped.Append(newLine).Append(indent);

            shaped.Append(branch.Header);
            if (isBraced)
            {
                shaped.Append(newLine).Append(indent).Append('{')
                    .Append(newLine).Append(inner).Append(branch.Body)
                    .Append(newLine).Append(indent).Append('}');
            }
            else if (indent.Length + branch.Header.Length + 1 + branch.Body.Length <= maxLineLength)
            {
                shaped.Append(' ').Append(branch.Body);
            }
            else
            {
                shaped.Append(newLine).Append(inner).Append(branch.Body);
            }
        }

        TextSpan span = TextSpan.FromBounds(ifStatement.SpanStart, branches[branches.Count - 1].End);
        string written = shaped.ToString();

        return new IfChain(span, written, string.Equals(text.ToString(span), written, StringComparison.Ordinal));
    }

    /// <summary>
    /// The <c>if</c>'s branch, each <c>else if</c>'s and the <c>else</c>'s, each null where it is not one this shapes.
    /// A branch's header runs from its first keyword to the last token before its body.
    /// </summary>
    private static IEnumerable<Branch?> ReadBranches(IfStatementSyntax first, SourceText text)
    {
        yield return Branch.Read(first.IfKeyword, first.CloseParenToken, first.Statement, text);

        ElseClauseSyntax? elseClause = first.Else;
        while (elseClause is not null)
        {
            if (elseClause.Statement is not IfStatementSyntax next)
            {
                yield return Branch.Read(elseClause.ElseKeyword, elseClause.ElseKeyword, elseClause.Statement, text);
                yield break;
            }

            yield return Branch.Read(elseClause.ElseKeyword, next.CloseParenToken, next.Statement, text);
            elseClause = next.Else;
        }
    }

    /// <summary>
    /// One branch of a chain as text: its header, its body's one statement with any comment after it, and where its
    /// body ends in the file, braces included.
    /// </summary>
    private sealed class Branch(string header, string body, int end)
    {
        public string Header { get; } = header;

        public string Body { get; } = body;

        public int End { get; } = end;

        /// <summary>
        /// The branch from <paramref name="start"/> to <paramref name="end"/> with its body, or null where its body is
        /// not one simple statement on one line, or moving it would lose a comment or a directive.
        /// </summary>
        public static Branch? Read(SyntaxToken start, SyntaxToken end, StatementSyntax body, SourceText text)
        {
            if (!IsOnOneLine(start.SpanStart, end.Span.End, text) || HasMoreThanSpace(end.TrailingTrivia)) return null;

            if (start.IsKind(SyntaxKind.ElseKeyword) && HasMoreThanSpace(start.LeadingTrivia)) return null;

            StatementSyntax statement = body;
            if (body is BlockSyntax block)
            {
                if (block.Statements.Count != 1 || HasMoreThanSpace(block.OpenBraceToken)
                    || HasMoreThanSpace(block.CloseBraceToken))
                {
                    return null;
                }

                statement = block.Statements[0];
            }

            // Only what cannot hold a statement of its own: a nested if would take the else after it as its own, a
            // declaration cannot be a body alone, and a loop is always braced.
            if (statement is not (ExpressionStatementSyntax or ReturnStatementSyntax or ThrowStatementSyntax
                or BreakStatementSyntax or ContinueStatementSyntax or YieldStatementSyntax))
            {
                return null;
            }

            if (!IsOnOneLine(statement.SpanStart, statement.Span.End, text)) return null;

            if (HasMoreThanSpace(statement.GetFirstToken().LeadingTrivia)) return null;

            // The body runs to the end of its line, with a comment after it, but no other code.
            TextLine line = text.Lines.GetLineFromPosition(statement.Span.End);
            string rest = text.ToString(TextSpan.FromBounds(statement.Span.End, line.End)).Trim();
            if (rest.Length > 0 && !rest.StartsWith("//", StringComparison.Ordinal)) return null;

            string header = text.ToString(TextSpan.FromBounds(start.SpanStart, end.Span.End));
            string statementText = text.ToString(TextSpan.FromBounds(statement.SpanStart, line.End)).TrimEnd();
            int bodyEnd = body is BlockSyntax braced
                ? braced.CloseBraceToken.Span.End
                : statement.SpanStart + statementText.Length;

            return new Branch(header, statementText, bodyEnd);
        }

        private static bool IsOnOneLine(int start, int end, SourceText text)
            => text.Lines.GetLineFromPosition(start).LineNumber == text.Lines.GetLineFromPosition(end).LineNumber;

        private static bool HasMoreThanSpace(SyntaxToken token)
            => HasMoreThanSpace(token.LeadingTrivia) || HasMoreThanSpace(token.TrailingTrivia);

        private static bool HasMoreThanSpace(SyntaxTriviaList trivia)
        {
            foreach (SyntaxTrivia item in trivia)
            {
                if (!item.IsKind(SyntaxKind.WhitespaceTrivia) && !item.IsKind(SyntaxKind.EndOfLineTrivia)) return true;
            }

            return false;
        }
    }
}
