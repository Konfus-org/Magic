using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Magic.Analyzers;

/// <summary>
/// MG0002: the null-forgiving operator <c>!</c> is not used. A value that may be null is checked, or carried in a
/// <c>Result</c>, so the compiler proves it is there (CODESTANDARDS Null).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoNullForgivingAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "MG0002";

    public static readonly DiagnosticDescriptor Rule = new(
        Id,
        "The null-forgiving operator is not allowed",
        "Check the value, or carry it in a Result, instead of '!'",
        "Null",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A '!' hides a null the compiler would otherwise report. Prove the value is there instead."
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            syntaxContext => syntaxContext.ReportDiagnostic(Diagnostic.Create(Rule, syntaxContext.Node.GetLocation())),
            SyntaxKind.SuppressNullableWarningExpression
        );
    }
}
