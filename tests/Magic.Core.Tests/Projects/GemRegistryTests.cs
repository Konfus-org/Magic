using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Magic.Files;
using Magic.Projects;
using Magic.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Log = Magic.Debugging.Log;

namespace Magic.Tests.Projects;

/// <summary>
/// A gem whose code references another gem: it shares that gem's one copy of its types, loads after it, and is refused
/// when the project doesn't list it. Each gem is compiled here and written where Magic finds gems, beside the tests.
/// </summary>
public sealed class GemRegistryTests
{
    private const string LenderSource = """
        namespace Lender;

        public sealed class LenderGem : Magic.Projects.IGem { }
        """;

    private const string BorrowerSource = """
        namespace Borrower;

        public sealed class BorrowerGem(Lender.LenderGem lender) : Magic.Projects.IGem
        {
            public Lender.LenderGem Lender { get; } = lender;
        }
        """;

    private static readonly FileSystem _files = new();

    [Fact]
    public async Task RegisterResolvesAReferencedGemToThatGemsOwnCopy()
    {
        await CreateGemsAsync("Lender", "Borrower").ConfigureAwait(true);

        List<GemInfo> gems = GemRegistry.Register(_files, ["Lender", "Borrower"], []);

        Type lender = gems.Single(gem => gem.Name == "Lender").Type;
        Type borrower = gems.Single(gem => gem.Name == "Borrower").Type;
        Assert.Same(lender, borrower.GetConstructors()[0].GetParameters()[0].ParameterType);
    }

    [Fact]
    public async Task RegisterPutsAGemAfterTheGemsItReferences()
    {
        await CreateGemsAsync("Lender", "Borrower").ConfigureAwait(true);

        List<GemInfo> gems = GemRegistry.Register(_files, ["Borrower", "Lender"], []);

        Assert.Equal(["Lender", "Borrower"], gems.ConvertAll(gem => gem.Name));
    }

    [Fact]
    public async Task RegisterRefusesAGemWhoseReferencedGemIsNotListed()
    {
        await CreateGemsAsync("Absent", "Orphan").ConfigureAwait(true);

        List<GemInfo> gems = GemRegistry.Register(_files, ["Orphan"], []);

        Assert.Empty(gems);
        Assert.Contains(
            Log.GetHistory(),
            line =>
                line.Message.Contains("Orphan", StringComparison.Ordinal)
                && line.Message.Contains("Absent", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// Compiles the lender gem as <paramref name="lender"/> and the borrower, which references it, as
    /// <paramref name="borrower"/>, each alone in its folder under <c>Gems/</c>.
    /// </summary>
    private static async Task CreateGemsAsync(string lender, string borrower)
    {
        byte[] lent = Compile(lender, LenderSource.Replace("Lender", lender, StringComparison.Ordinal), []);
        string borrowed = BorrowerSource
            .Replace("Lender", lender, StringComparison.Ordinal)
            .Replace("Borrower", borrower, StringComparison.Ordinal);
        byte[] borrowing = Compile(borrower, borrowed, [MetadataReference.CreateFromImage(lent)]);

        await WriteGemAsync(lender, lent).ConfigureAwait(false);
        await WriteGemAsync(borrower, borrowing).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="source"/> compiled as the library <paramref name="name"/> against the framework, Core and
    /// <paramref name="references"/>.
    /// </summary>
    private static byte[] Compile(string name, string source, ImmutableArray<MetadataReference> references)
    {
        string trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        IEnumerable<MetadataReference> framework = trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        CSharpCompilation compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            [.. framework, MetadataReference.CreateFromFile(typeof(IGem).Assembly.Location), .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join('\n', emitted.Diagnostics));

        return image.ToArray();
    }

    private static async Task WriteGemAsync(string name, byte[] image)
    {
        string folder = _files.Combine(AppContext.BaseDirectory, "Gems", name);
        Assert.True(_files.CreateDirectory(folder).Ok);
        Result written = await _files
            .WriteAsync(_files.Combine(folder, name + ".dll"), image, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        Assert.True(written.Ok, written.Error);
    }
}
