using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Magic.Analyzers;

/// <summary>
/// MG0001: a type's members are in the order the standards lay down. By kind, fields first and nested types last;
/// within a kind public before private; constants before other fields; static before instance; Dispose before the
/// other methods. Members that rank the same keep the order they are in. Reported once per type, at the first member
/// that belongs before the one above it; <see cref="MemberOrderCodeFix"/> puts the whole type in order.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MemberOrderAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "MG0001";

    public static readonly DiagnosticDescriptor Rule = new(
        Id,
        "Members are out of order",
        "'{0}' belongs before '{1}'",
        "Layout",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A type's members go by kind, then public before private, constants first, static before instance, Dispose first."
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Analyze,
            SyntaxKind.ClassDeclaration,
            SyntaxKind.StructDeclaration,
            SyntaxKind.RecordDeclaration,
            SyntaxKind.RecordStructDeclaration,
            SyntaxKind.InterfaceDeclaration
        );
    }

    /// <summary>
    /// Where <paramref name="member"/> ranks among its type's members: a smaller rank comes first, and equal ranks keep
    /// their order. Interface members with no modifier are public; any other member with none is private.
    /// </summary>
    public static int RankOf(MemberDeclarationSyntax member, bool inInterface)
    {
        int kind = KindOf(member);
        int access = AccessOf(member.Modifiers, inInterface);
        int constant = member.Modifiers.Any(SyntaxKind.ConstKeyword) ? 0 : 1;
        int isStatic = member.Modifiers.Any(SyntaxKind.StaticKeyword) ? 0 : 1;
        int dispose = member is MethodDeclarationSyntax { Identifier.ValueText: "Dispose" } ? 0 : 1;

        int rank = (kind * 8) + access;
        rank = (rank * 2) + constant;
        rank = (rank * 2) + isStatic;

        return (rank * 2) + dispose;
    }

    /// <summary>
    /// The name of <paramref name="member"/> as a diagnostic says it, and where that name is.
    /// </summary>
    public static (string Name, Location Location) NameOf(MemberDeclarationSyntax member)
    {
        SyntaxToken token = member switch
        {
            FieldDeclarationSyntax field => field.Declaration.Variables[0].Identifier,
            EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables[0].Identifier,
            BaseTypeDeclarationSyntax type => type.Identifier,
            DelegateDeclarationSyntax @delegate => @delegate.Identifier,
            MethodDeclarationSyntax method => method.Identifier,
            PropertyDeclarationSyntax property => property.Identifier,
            EventDeclarationSyntax @event => @event.Identifier,
            ConstructorDeclarationSyntax constructor => constructor.Identifier,
            DestructorDeclarationSyntax destructor => destructor.Identifier,
            IndexerDeclarationSyntax indexer => indexer.ThisKeyword,
            OperatorDeclarationSyntax @operator => @operator.OperatorToken,
            ConversionOperatorDeclarationSyntax conversion => conversion.Type.GetFirstToken(),
            _ => member.GetFirstToken(),
        };

        return (token.ValueText, token.GetLocation());
    }

    private static int KindOf(MemberDeclarationSyntax member) =>
        member switch
        {
            FieldDeclarationSyntax => 0,
            ConstructorDeclarationSyntax => 1,
            DestructorDeclarationSyntax => 2,
            EventFieldDeclarationSyntax or EventDeclarationSyntax => 3,
            PropertyDeclarationSyntax => 4,
            IndexerDeclarationSyntax => 5,
            ConversionOperatorDeclarationSyntax => 7,
            OperatorDeclarationSyntax => 8,
            MethodDeclarationSyntax => 9,
            DelegateDeclarationSyntax => 10,
            EnumDeclarationSyntax => 11,
            InterfaceDeclarationSyntax => 12,
            StructDeclarationSyntax => 13,
            RecordDeclarationSyntax record => record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? 13 : 14,
            ClassDeclarationSyntax => 14,
            _ => 6, // An extension block, or a kind this analyzer predates: between the data and the operators.
        };

    private static int AccessOf(SyntaxTokenList modifiers, bool inInterface)
    {
        bool isPublic = modifiers.Any(SyntaxKind.PublicKeyword);
        bool isInternal = modifiers.Any(SyntaxKind.InternalKeyword);
        bool isProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        bool isPrivate = modifiers.Any(SyntaxKind.PrivateKeyword);
        if (isPublic)
            return 0;
        if (isInternal && isProtected)
            return 2;
        if (isInternal)
            return 1;
        if (isProtected && isPrivate)
            return 4;
        if (isProtected)
            return 3;
        if (isPrivate)
            return 5;

        return inInterface ? 0 : 5;
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        TypeDeclarationSyntax type = (TypeDeclarationSyntax)context.Node;
        bool inInterface = type is InterfaceDeclarationSyntax;
        MemberDeclarationSyntax? previous = null;
        foreach (MemberDeclarationSyntax member in type.Members)
        {
            if (previous is not null && RankOf(member, inInterface) < RankOf(previous, inInterface))
            {
                (string name, Location location) = NameOf(member);
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, name, NameOf(previous).Name));

                return;
            }

            previous = member;
        }
    }
}
